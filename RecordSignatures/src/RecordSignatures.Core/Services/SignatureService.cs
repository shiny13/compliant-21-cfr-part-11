using AuthenticationCore.Shared.Roles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

public interface ISignatureService
{
    Task<SignatureRecord> SignAsync(SignRequest request, Operator caller, CancellationToken ct = default);

    Task<SignatureVerification> GetAsync(Guid signatureId, Operator caller, CancellationToken ct = default);

    /// <summary>All signatures on a record with the verification status of each.</summary>
    Task<RecordSignatureVerification> VerifyRecordAsync(Guid recordId, Operator caller, CancellationToken ct = default);

    Task<SignatureVerification> RevokeAsync(Guid signatureId, RevokeSignatureRequest request, Operator caller, CancellationToken ct = default);
}

/// <summary>
/// Applies and verifies electronic signatures. A signature manifests the signer's printed name, the date and time and the
/// meaning (11.50), and is sealed to one version and content hash of one record, in a per-record chain (11.70).
/// </summary>
public sealed class SignatureService(
    ISignerStore signers,
    ISignatureStore signatures,
    IUnitOfWork unitOfWork,
    IAuditTrailClient audit,
    ICredentialChallenge challenge,
    ISecurityAlertService alerts,
    SignatureSealer sealer,
    IOptions<SignatureOptions> options,
    TimeProvider clock,
    ILogger<SignatureService> logger) : ISignatureService
{
    public async Task<SignatureRecord> SignAsync(SignRequest request, Operator caller, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (request.RecordId == Guid.Empty)
        {
            throw new SignatureValidationException("A record id is required.");
        }
        if (request.RecordVersion < 1)
        {
            throw new SignatureValidationException("The record version the signer reviewed is required.");
        }
        var meaning = MeaningCatalog.Find(request.Meaning)
            ?? throw new SignatureValidationException($"The meaning must be one of: {string.Join(", ", MeaningCatalog.All.Select(m => m.Code))}.");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (comment is not null && comment.Length > settings.MaxCommentLength)
        {
            throw new SignatureValidationException($"The comment must not exceed {settings.MaxCommentLength} characters.");
        }

        // Only an enrolled, certified, current and active individual whose role allows this meaning may sign.
        var signer = await signers.FindByUserIdAsync(caller.UserId, ct)
            ?? throw new ForbiddenException("You are not enrolled as an electronic signer.");
        if (!string.Equals(signer.Username, caller.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenException("The token does not match the enrolled signer.");
        }
        if (!caller.Role.Satisfies(meaning.MinimumRole))
        {
            throw new ForbiddenException($"A '{meaning.Code}' signature requires the {meaning.MinimumRole} role or higher.");
        }
        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        signer.EnsureCanSign(now);

        // What you see is what you sign: the signed version must still be the current one.
        var record = await audit.GetRecordAsync(request.RecordId, caller.AccessToken, ct);
        if (record.IsDeleted)
        {
            throw new SignatureValidationException("A deleted record cannot be signed.");
        }
        if (record.Version != request.RecordVersion)
        {
            throw new ConflictException($"The record is at version {record.Version}, not {request.RecordVersion}. Review the current version before signing.");
        }

        await challenge.VerifyAsync(caller, request.Username, request.Password, request.RecordId, ct);

        // The challenge took time and may have crossed an expiry; the signed time must be the moment of signing.
        var signedUtc = TimestampPrecision.ToStorage(clock.GetUtcNow());
        await EnsureNotAlreadySignedAsync(request.RecordId, record.Version, caller.UserId, meaning.Code, ct);

        var contentHash = RecordContentHasher.Hash(record);
        var id = await audit.CreateRecordAsync(
            AuditFields.SignatureRecordType, $"{meaning.Code} signature by {signer.PrintedName}",
            AuditFields.ForNewSignature(record.Id, record.Version, contentHash, signer, meaning, comment, signedUtc),
            $"Electronic signature executed: {meaning.Code}", caller.AccessToken, ct);

        var last = await signatures.GetLastForRecordAsync(record.Id, ct);
        var unsealed = new SignatureRecord(
            id, record.Id, record.Version, contentHash, (last?.Sequence ?? 0) + 1,
            signer.UserId, signer.Username, signer.PrintedName, meaning.Code, meaning.Statement, comment, signedUtc,
            last?.SealHash ?? CanonicalHasher.Genesis, sealer.ActiveKeyId, "");
        var signature = unsealed with { SealHash = sealer.Compute(unsealed) };

        try
        {
            await signatures.AddAsync(signature, ct);
        }
        catch (Exception ex)
        {
            await AuditCompensation.VoidAsync(audit, logger, id,
                AuditFields.ForNewSignature(record.Id, record.Version, contentHash, signer, meaning, comment, signedUtc),
                caller.AccessToken, "the signature was not saved");
            if (ex is ConflictException)
            {
                throw new ConflictException("Another signature was applied to this record at the same moment. Review the record and sign again.", ex);
            }
            throw;
        }
        return signature;
    }

    public async Task<SignatureVerification> GetAsync(Guid signatureId, Operator caller, CancellationToken ct = default)
    {
        var signature = await signatures.GetAsync(signatureId, ct) ?? throw new NotFoundException($"Signature {signatureId} was not found.");
        var verification = await VerifyRecordAsync(signature.RecordId, caller, ct);
        return verification.Signatures.First(v => v.Signature.Id == signatureId);
    }

    public async Task<RecordSignatureVerification> VerifyRecordAsync(Guid recordId, Operator caller, CancellationToken ct = default)
    {
        var record = await audit.GetRecordAsync(recordId, caller.AccessToken, ct);
        var chain = await signatures.ListForRecordAsync(recordId, ct);
        var revocations = (await signatures.GetRevocationsAsync(chain.Select(s => s.Id).ToArray(), ct)).ToDictionary(r => r.SignatureId);

        var results = new List<SignatureVerification>();
        var problems = new List<string>();
        var expectedSequence = 1L;
        var previousSeal = CanonicalHasher.Genesis;

        foreach (var s in chain)
        {
            revocations.TryGetValue(s.Id, out var revocation);
            var (status, detail) = Classify(s, record, revocation, expectedSequence, previousSeal);
            if (status is SignatureStatus.SealInvalid or SignatureStatus.ChainBroken or SignatureStatus.ContentMismatch)
            {
                problems.Add($"Signature {s.Id} (sequence {s.Sequence}): {detail}");
            }
            results.Add(new SignatureVerification(s, revocation, status, detail));
            expectedSequence = s.Sequence + 1;
            previousSeal = s.SealHash;
        }

        var intact = problems.Count == 0;
        if (!intact)
        {
            await alerts.RaiseAsync(AlertType.SignatureIntegrityFailure, AlertSeverity.Critical, null, null,
                $"Signature integrity check failed for record {recordId}: {string.Join("; ", problems)}", caller, ct);
        }
        return new RecordSignatureVerification(record, results, intact, problems);
    }

    public async Task<SignatureVerification> RevokeAsync(Guid signatureId, RevokeSignatureRequest request, Operator caller, CancellationToken ct = default)
    {
        var reason = Validation.RequireReason(request.Reason, options.Value.MinReasonLength);
        var signature = await signatures.GetAsync(signatureId, ct) ?? throw new NotFoundException($"Signature {signatureId} was not found.");
        var revocation = new SignatureRevocation(signatureId, TimestampPrecision.ToStorage(clock.GetUtcNow()), caller.UserId, caller.Username, reason);

        await using (var tx = await unitOfWork.BeginTransactionAsync(ct))
        {
            await signatures.AddRevocationAsync(revocation, ct);
            await audit.UpdateRecordAsync(signatureId, AuditFields.ForRevokedSignature(signature, revocation), $"Electronic signature revoked: {reason}", caller.AccessToken, ct);
            await tx.CommitAsync(ct);
        }
        return new SignatureVerification(signature, revocation, SignatureStatus.Revoked, "Revoked: " + reason);
    }

    private (SignatureStatus Status, string? Detail) Classify(
        SignatureRecord s, RecordSnapshot record, SignatureRevocation? revocation, long expectedSequence, string previousSeal)
    {
        if (!sealer.Verify(s))
        {
            return (SignatureStatus.SealInvalid, "The signature does not match its seal; it was altered after signing.");
        }
        if (s.Sequence != expectedSequence || !string.Equals(s.PreviousSealHash, previousSeal, StringComparison.Ordinal))
        {
            return (SignatureStatus.ChainBroken, "A signature is missing from or out of order in the chain.");
        }
        if (s.RecordVersion == record.Version && !string.Equals(s.RecordContentHash, RecordContentHasher.Hash(record), StringComparison.Ordinal))
        {
            return (SignatureStatus.ContentMismatch, "The record has the signed version number but not the signed content.");
        }
        if (revocation is not null)
        {
            return (SignatureStatus.Revoked, "Revoked: " + revocation.Reason);
        }
        return s.RecordVersion != record.Version
            ? (SignatureStatus.RecordModifiedSinceSigning, $"Signed version {s.RecordVersion}; the record is now at version {record.Version}.")
            : (SignatureStatus.Valid, null);
    }

    private async Task EnsureNotAlreadySignedAsync(Guid recordId, int version, string userId, string meaningCode, CancellationToken ct)
    {
        var existing = (await signatures.ListForRecordAsync(recordId, ct))
            .Where(s => s.RecordVersion == version && s.SignerUserId == userId && s.MeaningCode == meaningCode)
            .ToList();
        if (existing.Count == 0)
        {
            return;
        }
        var revoked = (await signatures.GetRevocationsAsync(existing.Select(s => s.Id).ToArray(), ct)).Select(r => r.SignatureId).ToHashSet();
        if (existing.Any(s => !revoked.Contains(s.Id)))
        {
            throw new ConflictException("You have already applied this signature to this version of the record.");
        }
    }
}

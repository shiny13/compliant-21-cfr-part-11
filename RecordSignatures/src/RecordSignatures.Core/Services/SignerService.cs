using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

public interface ISignerService
{
    Task<Signer> EnrollAsync(EnrollSignerRequest request, Operator administrator, CancellationToken ct = default);

    Task<Signer> AcceptCertificationAsync(CertifyRequest request, Operator self, CancellationToken ct = default);

    Task<SignerActionResult> SuspendAsync(string userId, SuspendSignerRequest request, Operator administrator, CancellationToken ct = default);

    Task<SignerActionResult> ReinstateAsync(string userId, ReinstateSignerRequest request, Operator administrator, CancellationToken ct = default);

    Task<SignerActionResult> RevokeAsync(string userId, RevokeSignerRequest request, Operator administrator, CancellationToken ct = default);

    Task<SignerActionResult> ReviewAsync(string userId, ReviewSignerRequest request, Operator administrator, CancellationToken ct = default);

    Task<Signer> GetAsync(string userId, CancellationToken ct = default);

    Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, int skip, int take, CancellationToken ct = default);

    Task<string> GetCertificationAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Manages who may sign (11.100) and keeps that authority under control (11.200, 11.300): enrollment with identity
/// verification, individual certification, periodic re-verification, and deauthorization of lost or compromised
/// credentials. Every change is written to the RecordAuditTrails audit trail with its reason.
/// </summary>
public sealed class SignerService(
    ISignerStore signers,
    IUnitOfWork unitOfWork,
    IAuditTrailClient audit,
    ICredentialChallenge challenge,
    ISessionRevoker sessions,
    ISecurityAlertService alerts,
    IOptions<SignatureOptions> options,
    TimeProvider clock,
    ILogger<SignerService> logger) : ISignerService
{
    private const int MaxIdLength = 200;
    private const int MaxNameLength = 200;
    private const int MaxEvidenceLength = 500;

    public async Task<Signer> EnrollAsync(EnrollSignerRequest request, Operator administrator, CancellationToken ct = default)
    {
        var userId = Validation.RequireText(request.UserId, "User id", MaxIdLength);
        var username = Validation.RequireText(request.Username, "Username", MaxIdLength).ToLowerInvariant();
        var printedName = Validation.RequireText(request.PrintedName, "Printed name", MaxNameLength);
        var employeeId = Validation.RequireText(request.EmployeeId, "Employee id", MaxIdLength);
        var jobTitle = string.IsNullOrWhiteSpace(request.JobTitle) ? null : Validation.RequireText(request.JobTitle, "Job title", MaxNameLength);
        EnsureNotSelf(administrator, userId);

        // Identity uniqueness (11.100(a)): one signature per individual, never shared and never reassigned.
        if (await signers.FindByUserIdAsync(userId, ct) is { } existing)
        {
            throw new ConflictException(existing.Status == SignerStatus.Revoked
                ? "This identity was revoked; an electronic signature is never reassigned or reused."
                : "This user is already enrolled as an electronic signer.");
        }
        if (await signers.FindByUsernameAsync(username, ct) is not null)
        {
            throw new ConflictException("This identification code is already assigned to an electronic signer.");
        }
        if (await signers.EmployeeIdInUseAsync(employeeId, ct))
        {
            throw new ConflictException("This individual already has a live electronic signature enrollment.");
        }

        var now = Now();
        var signer = new Signer
        {
            UserId = userId,
            Username = username,
            PrintedName = printedName,
            EmployeeId = employeeId,
            JobTitle = jobTitle,
            Status = SignerStatus.PendingCertification,
            StatusReason = "Enrolled",
            StatusChangedUtc = now,
            EnrolledUtc = now,
            EnrolledByUserId = administrator.UserId,
            EnrolledByUsername = administrator.Username,
            Identity = BuildIdentity(request.Identity, administrator, now),
        };
        signer.ScheduleReview(now, options.Value.ReviewIntervalMonths);

        signer.Id = await audit.CreateRecordAsync(
            AuditFields.SignerRecordType, $"Signer: {printedName}", AuditFields.ForSigner(signer),
            "Electronic signer enrolled after identity verification", administrator.AccessToken, ct);
        try
        {
            await signers.AddAsync(signer, ct);
        }
        catch
        {
            await AuditCompensation.VoidAsync(audit, logger, signer.Id, AuditFields.ForSigner(signer), administrator.AccessToken, "enrollment was not saved");
            throw;
        }
        return signer;
    }

    public async Task<Signer> AcceptCertificationAsync(CertifyRequest request, Operator self, CancellationToken ct = default)
    {
        var signer = await signers.FindByUserIdAsync(self.UserId, ct)
            ?? throw new ForbiddenException("You are not enrolled as an electronic signer.");
        if (!string.Equals(request.StatementVersion, CertificationStatement.CurrentVersion, StringComparison.Ordinal))
        {
            throw new SignatureValidationException($"The current certification statement is version {CertificationStatement.CurrentVersion}.");
        }
        if (signer.Status != SignerStatus.PendingCertification)
        {
            throw new ConflictException("There is no pending certification to accept.");
        }

        await challenge.VerifyAsync(self, request.Username, request.Password, null, ct);

        signer.AcceptCertification(request.StatementVersion, Now());
        await PersistAsync(signer, self, "Signer accepted the electronic signature certification", ct);
        return signer;
    }

    public async Task<SignerActionResult> SuspendAsync(string userId, SuspendSignerRequest request, Operator administrator, CancellationToken ct = default)
    {
        var reason = Validation.RequireReason(request.Reason, options.Value.MinReasonLength);
        var signer = await LoadForAdminAsync(userId, administrator, ct);

        signer.Suspend(reason, Now());
        await PersistAsync(signer, administrator, $"Electronic signature suspended: {reason}", ct);
        return await FinishDeauthorizationAsync(signer, "suspended", reason, request.RevokeSessions, administrator, ct);
    }

    public async Task<SignerActionResult> ReinstateAsync(string userId, ReinstateSignerRequest request, Operator administrator, CancellationToken ct = default)
    {
        var reason = Validation.RequireReason(request.Reason, options.Value.MinReasonLength);
        var signer = await LoadForAdminAsync(userId, administrator, ct);
        var now = Now();

        signer.Reinstate(reason, BuildIdentity(request.Identity, administrator, now), now, options.Value.ReviewIntervalMonths);
        await PersistAsync(signer, administrator, $"Electronic signature reinstated after re-verification: {reason}", ct);
        return new SignerActionResult(signer, null);
    }

    public async Task<SignerActionResult> RevokeAsync(string userId, RevokeSignerRequest request, Operator administrator, CancellationToken ct = default)
    {
        var reason = Validation.RequireReason(request.Reason, options.Value.MinReasonLength);
        var signer = await LoadForAdminAsync(userId, administrator, ct);

        signer.Revoke(reason, Now());
        await PersistAsync(signer, administrator, $"Electronic signature permanently revoked: {reason}", ct);
        return await FinishDeauthorizationAsync(signer, "revoked", reason, request.RevokeSessions, administrator, ct);
    }

    public async Task<SignerActionResult> ReviewAsync(string userId, ReviewSignerRequest request, Operator administrator, CancellationToken ct = default)
    {
        var reason = Validation.RequireReason(request.Reason, options.Value.MinReasonLength);
        var signer = await LoadForAdminAsync(userId, administrator, ct);
        var now = Now();

        signer.Review(reason, BuildIdentity(request.Identity, administrator, now), now, options.Value.ReviewIntervalMonths);
        await PersistAsync(signer, administrator, $"Periodic review completed: {reason}", ct);
        return new SignerActionResult(signer, null);
    }

    public async Task<Signer> GetAsync(string userId, CancellationToken ct = default) =>
        await signers.FindByUserIdAsync(userId, ct) ?? throw new NotFoundException("No electronic signer is enrolled for that user.");

    public Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, int skip, int take, CancellationToken ct = default)
    {
        var (s, t) = Paging.Normalize(skip, take);
        return signers.ListAsync(status, reviewDueOnly, Now(), s, t, ct);
    }

    public async Task<string> GetCertificationAsync(string userId, CancellationToken ct = default) =>
        CertificationStatement.Render(await GetAsync(userId, ct), options.Value.OrganizationName);

    private async Task<Signer> LoadForAdminAsync(string userId, Operator administrator, CancellationToken ct)
    {
        EnsureNotSelf(administrator, userId);
        return await GetAsync(userId, ct);
    }

    /// <summary>Writes the change and its audit entry as one unit: if RecordAuditTrails rejects the entry, nothing is saved.</summary>
    private async Task PersistAsync(Signer signer, Operator caller, string reason, CancellationToken ct)
    {
        await using var tx = await unitOfWork.BeginTransactionAsync(ct);
        await signers.UpdateAsync(signer, ct);
        await audit.UpdateRecordAsync(signer.Id, AuditFields.ForSigner(signer), reason, caller.AccessToken, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Loss management (11.300(c)): cut the sessions, and tell the security unit.</summary>
    private async Task<SignerActionResult> FinishDeauthorizationAsync(
        Signer signer, string verb, string reason, bool revokeSessions, Operator administrator, CancellationToken ct)
    {
        bool? revoked = null;
        if (revokeSessions)
        {
            try
            {
                revoked = await sessions.RevokeUserSessionsAsync(signer.UserId, administrator.AccessToken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not revoke sessions of {UserId}", signer.UserId);
                revoked = false;
            }
        }

        await alerts.RaiseAsync(AlertType.SignerDeauthorized, AlertSeverity.Warning, signer.UserId, signer.Username,
            $"Electronic signature {verb} by {administrator.Username}: {reason}" +
            (revoked == false ? " (sessions could NOT be revoked automatically)" : ""), administrator, ct);
        return new SignerActionResult(signer, revoked);
    }

    private static void EnsureNotSelf(Operator administrator, string userId)
    {
        if (string.Equals(administrator.UserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("An administrator cannot administer their own electronic signature; a second individual must do it (21 CFR 11.200(a)(3)).");
        }
    }

    private IdentityVerification BuildIdentity(IdentityVerificationRequest? request, Operator administrator, DateTime now)
    {
        if (request is null || !Enum.IsDefined(request.Method))
        {
            throw new SignatureValidationException("Identity verification (method and evidence) is required (21 CFR 11.100(b)).");
        }
        var evidence = Validation.RequireText(request.EvidenceReference, "Identity evidence reference", MaxEvidenceLength);
        return new IdentityVerification(request.Method, evidence, administrator.UserId, administrator.Username, now);
    }

    private DateTime Now() => TimestampPrecision.ToStorage(clock.GetUtcNow());
}

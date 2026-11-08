using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

public class SignatureServiceTests
{
    private readonly Harness _h = new();

    private async Task<(Guid Record, SignatureRecord Signature)> SignedAsync(string meaning = MeaningCatalog.Authorship, Operator? who = null)
    {
        who ??= _h.Tech;
        if (_h.Data.Signers.All(s => s.UserId != who.UserId))
        {
            await _h.ActivateAsync(who);
        }
        var record = _h.Audit.Records.Keys.FirstOrDefault(k => _h.Audit.Records[k].Type == "BatchRecord");
        if (record == Guid.Empty)
        {
            record = _h.Audit.AddRecord();
        }
        var version = _h.Audit.Records[record].Version;
        return (record, await _h.Signatures.SignAsync(_h.Sign(record, version, meaning, who), who));
    }

    // ---- 11.50: manifestation ----

    [Fact]
    public async Task Sign_ManifestsPrintedNameDateTimeAndMeaning()
    {
        await _h.ActivateAsync(_h.Tech, "Tina Technician");
        var record = _h.Audit.AddRecord();
        _h.Clock.Advance(TimeSpan.FromMinutes(5));

        var s = await _h.Signatures.SignAsync(_h.Sign(record, 1, "authorship", comment: "  I wrote this  "), _h.Tech);

        Assert.Equal("Tina Technician", s.SignerPrintedName);
        Assert.Equal("tina.tech", s.SignerUsername);
        Assert.Equal(Harness.Start.UtcDateTime.AddMinutes(5), s.SignedUtc);
        Assert.Equal("Authorship", s.MeaningCode);
        Assert.Equal(MeaningCatalog.Find("Authorship")!.Statement, s.MeaningText);
        Assert.Equal("I wrote this", s.Comment);
    }

    // ---- 11.70: linking ----

    [Fact]
    public async Task Sign_BindsTheSignatureToTheExactRecordVersionAndContent()
    {
        var (record, s) = await SignedAsync();
        var snapshot = await _h.Audit.GetRecordAsync(record, "t");

        Assert.Equal(record, s.RecordId);
        Assert.Equal(1, s.RecordVersion);
        Assert.Equal(RecordContentHasher.Hash(snapshot), s.RecordContentHash);
        Assert.Equal(1, s.Sequence);
        Assert.Equal(CanonicalHasher.Genesis, s.PreviousSealHash);
        Assert.True(_h.Sealer.Verify(s));
        Assert.Equal("k1", s.SealKeyId);
    }

    [Fact]
    public async Task Sign_WritesAnAuditRecord_UnderTheSignersOwnToken_AndUsesItsIdAsTheSignatureId()
    {
        var (_, s) = await SignedAsync();
        var audit = Assert.Single(_h.Audit.OfType(AuditFields.SignatureRecordType));
        Assert.Equal(s.Id, audit.Id);
        Assert.Equal("tok-tina", audit.Tokens.Single());
        Assert.Equal("Executed", audit.Fields["status"]);
        Assert.Equal(s.RecordContentHash, audit.Fields["signedContentHash"]);
        Assert.Equal("Printed tina.tech", audit.Fields["signerPrintedName"]);
        Assert.Equal("Authorship", audit.Fields["meaning"]);
        Assert.Equal(TimestampPrecision.Canonical(s.SignedUtc), audit.Fields["signedUtc"]);
        Assert.Contains("Authorship", audit.Reasons.Single());
    }

    [Fact]
    public async Task Sign_ChainsSignaturesOnTheSameRecord()
    {
        var (record, first) = await SignedAsync();
        await _h.ActivateAsync(_h.Manager);
        var second = await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Approval, _h.Manager), _h.Manager);

        Assert.Equal(2, second.Sequence);
        Assert.Equal(first.SealHash, second.PreviousSealHash);
        Assert.NotEqual(first.SealHash, second.SealHash);
    }

    [Fact]
    public async Task Sign_ASecondMeaningByTheSamePersonIsAllowed()
    {
        var (record, _) = await SignedAsync();
        var s = await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Responsibility), _h.Tech);
        Assert.Equal(2, s.Sequence);
    }

    [Fact]
    public async Task Sign_WhatYouSeeIsWhatYouSign_StaleVersionIsRejected()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Audit.Modify(record);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signatures.SignAsync(_h.Sign(record, 1), _h.Tech));
        Assert.Contains("version 2, not 1", ex.Message);
        Assert.Empty(_h.Data.Signatures);
        Assert.Empty(_h.Audit.OfType(AuditFields.SignatureRecordType));
    }

    [Fact]
    public async Task Sign_ADuplicateOfTheSameSignatureIsRejected_UntilItIsRevoked()
    {
        var (record, first) = await SignedAsync();
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signatures.SignAsync(_h.Sign(record, 1), _h.Tech));

        await _h.Signatures.RevokeAsync(first.Id, new RevokeSignatureRequest("Signed by mistake"), _h.Manager);
        var again = await _h.Signatures.SignAsync(_h.Sign(record, 1), _h.Tech);
        Assert.Equal(2, again.Sequence);
    }

    [Fact]
    public async Task Sign_AfterTheRecordChanges_ANewVersionCanBeSignedAgain()
    {
        var (record, _) = await SignedAsync();
        _h.Audit.Modify(record);
        var s = await _h.Signatures.SignAsync(_h.Sign(record, 2), _h.Tech);
        Assert.Equal(2, s.RecordVersion);
    }

    // ---- authorization to sign ----

    [Fact]
    public async Task Sign_RequiresEnrollment()
    {
        var record = _h.Audit.AddRecord();
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Contains("not enrolled", ex.Message);
    }

    [Fact]
    public async Task Sign_RequiresCertificationToHaveBeenAccepted()
    {
        await _h.Signers.EnrollAsync(Harness.EnrollRequest(_h.Tech), _h.Admin);
        var record = _h.Audit.AddRecord();
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
    }

    [Fact]
    public async Task Sign_IsBlockedWhileSuspended_AndAfterRevocation()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost"), _h.Admin);
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        await _h.Signers.ReinstateAsync("id-tina", new ReinstateSignerRequest("Badge found", Harness.Identity), _h.Admin);
        await _h.Signatures.SignAsync(_h.Sign(record), _h.Tech);
        await _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("Left the company"), _h.Admin);
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Responsibility), _h.Tech));
    }

    [Fact]
    public async Task Sign_IsBlockedWhenThePeriodicReviewIsOverdue_AndResumesAfterReview()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Clock.Advance(TimeSpan.FromDays(366));
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Contains("overdue", ex.Message);
        await _h.Signers.ReviewAsync("id-tina", new ReviewSignerRequest("Annual recertification", Harness.Identity), _h.Admin);
        await _h.Signatures.SignAsync(_h.Sign(record), _h.Tech);
    }

    [Fact]
    public async Task Sign_MeaningNeedsTheRightRole()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Approval), _h.Tech));
        Assert.Contains("LabManager", ex.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Review), _h.Tech));
        await _h.ActivateAsync(_h.Admin is null ? _h.Manager : _h.Reviewer);
        await _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Review, who: _h.Reviewer), _h.Reviewer);
    }

    [Fact]
    public async Task Sign_TheTokensUsernameMustMatchTheEnrollment()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        var renamed = new Operator("id-tina", "someone.else", AuthenticationCore.Shared.Roles.UserRole.LabTechnician, "tok");
        _h.Auth.Add("someone.else", Harness.Password, "id-tina");
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signatures.SignAsync(_h.Sign(record, who: renamed), renamed));
    }

    // ---- 11.200: both components, every time ----

    [Fact]
    public async Task Sign_RequiresThePasswordEveryTime()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        var before = _h.Auth.Calls;
        await _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Authorship), _h.Tech);
        await _h.Signatures.SignAsync(_h.Sign(record, meaning: MeaningCatalog.Responsibility), _h.Tech);
        Assert.Equal(before + 2, _h.Auth.Calls);
    }

    [Fact]
    public async Task Sign_WithAWrongPassword_SignsNothing_AndLeavesNoAuditRecord()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => _h.Signatures.SignAsync(_h.Sign(record, password: "wrong"), _h.Tech));
        Assert.Empty(_h.Data.Signatures);
        Assert.Empty(_h.Audit.OfType(AuditFields.SignatureRecordType));
        Assert.Equal(AlertType.FailedSignatureAuthentication, _h.Sink.Alerts.Last().Type);
    }

    [Fact]
    public async Task Sign_AsSomeoneElse_IsRefused_AndAlerted()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        var request = _h.Sign(record) with { Username = "rob.reviewer", Password = Harness.Password };
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => _h.Signatures.SignAsync(request, _h.Tech));
        Assert.Equal(AlertType.IdentityMismatch, _h.Sink.Alerts.Last().Type);
        Assert.Empty(_h.Data.Signatures);
    }

    [Fact]
    public async Task Sign_IsLockedAfterRepeatedFailures()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => _h.Signatures.SignAsync(_h.Sign(record, password: "wrong"), _h.Tech));
        }
        await Assert.ThrowsAsync<SignerLockedException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
    }

    [Fact]
    public async Task Sign_FailsClosed_WhenTheIdentityProviderIsDown()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Auth.Unavailable = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Empty(_h.Data.Signatures);
    }

    // ---- input and record checks ----

    [Fact]
    public async Task Sign_ValidatesTheRequest()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(Guid.Empty), _h.Tech));
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(record, version: 0), _h.Tech));
        var ex = await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(record, meaning: "Witnessed"), _h.Tech));
        Assert.Contains("Authorship", ex.Message);
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(record, comment: new string('x', 501)), _h.Tech));
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(record) with { Meaning = "" }, _h.Tech));
    }

    [Fact]
    public async Task Sign_BlankCommentIsStoredAsNull()
    {
        await _h.ActivateAsync(_h.Tech);
        var s = await _h.Signatures.SignAsync(_h.Sign(_h.Audit.AddRecord(), comment: "   "), _h.Tech);
        Assert.Null(s.Comment);
    }

    [Fact]
    public async Task Sign_UnknownRecord_IsNotFound()
    {
        await _h.ActivateAsync(_h.Tech);
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signatures.SignAsync(_h.Sign(Guid.NewGuid()), _h.Tech));
    }

    [Fact]
    public async Task Sign_DeletedRecord_IsRejected()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Audit.Records[record].IsDeleted = true;
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
    }

    // ---- failure handling around the audit trail ----

    [Fact]
    public async Task Sign_WhenTheAuditTrailIsDown_SignsNothing()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Audit.FailCreate = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Empty(_h.Data.Signatures);
    }

    [Fact]
    public async Task Sign_WhenTheSaveFails_TheAuditRecordIsMarkedVoid()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Data.SignatureAddException = new InvalidOperationException("disk full");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        var audit = Assert.Single(_h.Audit.OfType(AuditFields.SignatureRecordType));
        Assert.Equal("NotPersisted", audit.Fields["status"]);
        Assert.Contains("Marked void", audit.Reasons.Last());
    }

    [Fact]
    public async Task Sign_WhenTwoSignersCollide_ReportsAConflict_AndVoidsTheLoser()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Data.FailSignatureAdd = true;
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Contains("same moment", ex.Message);
        Assert.Equal("NotPersisted", _h.Audit.OfType(AuditFields.SignatureRecordType).Single().Fields["status"]);
    }

    [Fact]
    public async Task Sign_WhenVoidingFails_TheOriginalErrorStillSurfaces()
    {
        await _h.ActivateAsync(_h.Tech);
        var record = _h.Audit.AddRecord();
        _h.Data.SignatureAddException = new InvalidOperationException("disk full");
        _h.Audit.FailUpdate = true;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Signatures.SignAsync(_h.Sign(record), _h.Tech));
        Assert.Equal("disk full", ex.Message);
    }

    // ---- verification ----

    [Fact]
    public async Task Verify_AFreshSignature_IsValid()
    {
        var (record, s) = await SignedAsync();
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.True(result.IsIntact);
        Assert.Empty(result.Problems);
        var v = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.Valid, v.Status);
        Assert.Equal(s.Id, v.Signature.Id);
        Assert.Null(v.Detail);
    }

    [Fact]
    public async Task Verify_ARecordWithNoSignatures_IsIntactAndEmpty()
    {
        var record = _h.Audit.AddRecord();
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.True(result.IsIntact);
        Assert.Empty(result.Signatures);
    }

    [Fact]
    public async Task Verify_UnknownRecord_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signatures.VerifyRecordAsync(Guid.NewGuid(), _h.Reviewer));
    }

    [Fact]
    public async Task Verify_ChangingTheRecordAfterSigning_IsShown_ButIsNotTampering()
    {
        var (record, _) = await SignedAsync();
        _h.Audit.Modify(record);
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        var v = Assert.Single(result.Signatures);
        Assert.Equal(SignatureStatus.RecordModifiedSinceSigning, v.Status);
        Assert.Contains("version 1", v.Detail);
        Assert.True(result.IsIntact);
        Assert.Empty(_h.Data.Alerts);
    }

    [Fact]
    public async Task Verify_SameVersionDifferentContent_IsTamperingAndAlerts()
    {
        var (record, _) = await SignedAsync();
        _h.Audit.Records[record].Fields["pH"] = "1.0"; // changed behind the version counter's back
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.Equal(SignatureStatus.ContentMismatch, Assert.Single(result.Signatures).Status);
        Assert.False(result.IsIntact);
        Assert.Single(result.Problems);
        var alert = Assert.Single(_h.Data.Alerts);
        Assert.Equal(AlertType.SignatureIntegrityFailure, alert.Type);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    [Fact]
    public async Task Verify_ADatabaseEditOfASignature_BreaksItsSeal()
    {
        var (record, s) = await SignedAsync();
        _h.Data.Signatures[0] = s with { SignerPrintedName = "Mallory Mallet" };
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.Equal(SignatureStatus.SealInvalid, Assert.Single(result.Signatures).Status);
        Assert.False(result.IsIntact);
    }

    [Fact]
    public async Task Verify_ACopiedSignatureOnAnotherRecord_BreaksItsSeal()
    {
        var (_, s) = await SignedAsync();
        var other = _h.Audit.AddRecord(title: "Other");
        _h.Data.Signatures.Add(s with { RecordId = other });
        var result = await _h.Signatures.VerifyRecordAsync(other, _h.Reviewer);
        Assert.Equal(SignatureStatus.SealInvalid, Assert.Single(result.Signatures).Status);
    }

    [Fact]
    public async Task Verify_ARemovedSignature_BreaksTheChain()
    {
        var (record, _) = await SignedAsync();
        await _h.ActivateAsync(_h.Manager);
        await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Approval, _h.Manager), _h.Manager);
        _h.Data.Signatures.RemoveAt(0); // the first signature is excised
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.Equal(SignatureStatus.ChainBroken, Assert.Single(result.Signatures).Status);
        Assert.False(result.IsIntact);
    }

    [Fact]
    public async Task Verify_AMissingMiddleSignature_IsReportedOnceAndLaterOnesStillCheck()
    {
        var (record, _) = await SignedAsync();
        await _h.ActivateAsync(_h.Manager);
        await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Approval, _h.Manager), _h.Manager);
        await _h.ActivateAsync(_h.Reviewer);
        await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Review, _h.Reviewer), _h.Reviewer);
        _h.Data.Signatures.RemoveAt(1);
        var result = await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer);
        Assert.Equal([SignatureStatus.Valid, SignatureStatus.ChainBroken], result.Signatures.Select(v => v.Status));
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task Verify_SealedWithAnUnknownKey_IsInvalid()
    {
        var (record, s) = await SignedAsync();
        _h.Data.Signatures[0] = s with { SealKeyId = "retired-and-lost" };
        Assert.Equal(SignatureStatus.SealInvalid, (await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer)).Signatures.Single().Status);
    }

    // ---- reading and revoking ----

    [Fact]
    public async Task Get_ReturnsTheSignatureWithItsStatus()
    {
        var (_, s) = await SignedAsync();
        var v = await _h.Signatures.GetAsync(s.Id, _h.Reviewer);
        Assert.Equal(s.Id, v.Signature.Id);
        Assert.Equal(SignatureStatus.Valid, v.Status);
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signatures.GetAsync(Guid.NewGuid(), _h.Reviewer));
    }

    [Fact]
    public async Task Revoke_WithdrawsASignature_RecordsWhy_AndKeepsTheOriginalRow()
    {
        var (record, s) = await SignedAsync();
        await _h.ActivateAsync(_h.Manager);
        _h.Clock.Advance(TimeSpan.FromHours(1));

        var result = await _h.Signatures.RevokeAsync(s.Id, new RevokeSignatureRequest("Signed with a compromised password"), _h.Manager);

        Assert.Equal(SignatureStatus.Revoked, result.Status);
        Assert.Equal("mia.manager", result.Revocation!.RevokedByUsername);
        Assert.Equal(Harness.Start.UtcDateTime.AddHours(1), result.Revocation.RevokedUtc);
        Assert.Equal(s, _h.Data.Signatures.Single(x => x.Id == s.Id));
        var audit = _h.Audit.OfType(AuditFields.SignatureRecordType).Single();
        Assert.Equal("Revoked", audit.Fields["status"]);
        Assert.Equal("Signed with a compromised password", audit.Fields["revocationReason"]);
        Assert.Equal("tok-mia", audit.Tokens.Last());
        Assert.Equal(SignatureStatus.Revoked, (await _h.Signatures.VerifyRecordAsync(record, _h.Reviewer)).Signatures.Single().Status);
    }

    [Fact]
    public async Task Revoke_RequiresAReason_AndAnExistingSignature_AndOnlyOnce()
    {
        var (_, s) = await SignedAsync();
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signatures.RevokeAsync(s.Id, new RevokeSignatureRequest("no"), _h.Manager));
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signatures.RevokeAsync(Guid.NewGuid(), new RevokeSignatureRequest("does not exist"), _h.Manager));
        await _h.Signatures.RevokeAsync(s.Id, new RevokeSignatureRequest("first revocation"), _h.Manager);
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signatures.RevokeAsync(s.Id, new RevokeSignatureRequest("second revocation"), _h.Manager));
    }

    [Fact]
    public async Task Revoke_RollsBack_WhenTheAuditTrailRejectsTheEntry()
    {
        var (_, s) = await SignedAsync();
        _h.Audit.FailUpdate = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => _h.Signatures.RevokeAsync(s.Id, new RevokeSignatureRequest("will not stick"), _h.Manager));
        Assert.Empty(_h.Data.Revocations);
        Assert.Equal(1, _h.Uow.Rollbacks);
    }

    // ---- reports (11.50(b)) ----

    [Theory]
    [InlineData("json", ReportFormatKind.Json)]
    [InlineData("TEXT", ReportFormatKind.Text)]
    [InlineData("Html", ReportFormatKind.Html)]
    public async Task Report_ShowsNameDateTimeAndMeaning_InEveryFormat(string format, ReportFormatKind kind)
    {
        await _h.ActivateAsync(_h.Tech, "Tina Technician");
        var record = _h.Audit.AddRecord(title: "Batch <7>");
        await _h.Signatures.SignAsync(_h.Sign(record, comment: "looks good"), _h.Tech);

        Reporting.SignatureReportExporter.TryParseFormat(format, out var parsed);
        var report = await _h.Reports.ExportAsync(record, parsed, _h.Reviewer);

        Assert.Contains("Tina Technician", report.Content);
        Assert.Contains("2026-03-01T12:00:00.000000Z", report.Content);
        Assert.Contains("Authorship", report.Content);
        Assert.Contains("looks good", report.Content);
        Assert.Matches("^[0-9a-f]{64}$", report.Sha256);
        Assert.Equal(kind switch { ReportFormatKind.Json => "application/json", ReportFormatKind.Text => "text/plain", _ => "text/html" }, report.ContentType);
        if (kind == ReportFormatKind.Html)
        {
            Assert.Contains("Batch &lt;7&gt;", report.Content);
            Assert.DoesNotContain("Batch <7>", report.Content);
        }
    }

    public enum ReportFormatKind { Json, Text, Html }
}

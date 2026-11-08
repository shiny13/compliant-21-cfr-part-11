using Microsoft.Extensions.Logging.Abstractions;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

public class SignerServiceTests
{
    private readonly Harness _h = new();

    private Task<Signer> Enroll(Operator? user = null) => _h.Signers.EnrollAsync(Harness.EnrollRequest(user ?? _h.Tech), _h.Admin);

    // ---- enrollment (11.100(a), 11.100(b)) ----

    [Fact]
    public async Task Enroll_CreatesAPendingSigner_WithVerifiedIdentity_AndAnAuditRecord()
    {
        var signer = await Enroll();

        Assert.Equal(SignerStatus.PendingCertification, signer.Status);
        Assert.Equal("id-tina", signer.UserId);
        Assert.Equal("tina.tech", signer.Username);
        Assert.Equal("Printed tina.tech", signer.PrintedName);
        Assert.Equal(Harness.Start.UtcDateTime, signer.EnrolledUtc);
        Assert.Equal(Harness.Start.UtcDateTime.AddMonths(12), signer.NextReviewDueUtc);
        Assert.Equal("adam.admin", signer.Identity.VerifiedByUsername);
        Assert.Equal("id-adam", signer.Identity.VerifiedByUserId);
        Assert.Equal(IdentityVerificationMethod.GovernmentIssuedId, signer.Identity.Method);
        Assert.Equal(1, signer.Version);

        var audit = Assert.Single(_h.Audit.OfType(AuditFields.SignerRecordType));
        Assert.Equal(signer.Id, audit.Id);
        Assert.Equal("tok-adam", audit.Tokens.Single());
        Assert.Equal("PendingCertification", audit.Fields["status"]);
        Assert.Equal("id-tina", audit.Fields["userId"]);
        Assert.Contains("identity verification", audit.Reasons.Single());
        Assert.Single(_h.Data.Signers);
    }

    [Fact]
    public async Task Enroll_NormalizesTheUsername_AndTrimsText()
    {
        var request = Harness.EnrollRequest(_h.Tech) with { Username = "  TINA.Tech ", PrintedName = "  Tina Tech  ", JobTitle = "   " };
        var signer = await _h.Signers.EnrollAsync(request, _h.Admin);
        Assert.Equal("tina.tech", signer.Username);
        Assert.Equal("Tina Tech", signer.PrintedName);
        Assert.Null(signer.JobTitle);
    }

    public static TheoryData<Func<EnrollSignerRequest, EnrollSignerRequest>> BadRequests() => new()
    {
        r => r with { UserId = " " },
        r => r with { Username = "" },
        r => r with { PrintedName = "" },
        r => r with { EmployeeId = "" },
        r => r with { PrintedName = new string('x', 201) },
        r => r with { JobTitle = new string('x', 201) },
        r => r with { Identity = null! },
        r => r with { Identity = new IdentityVerificationRequest((IdentityVerificationMethod)99, "ev") },
        r => r with { Identity = new IdentityVerificationRequest(IdentityVerificationMethod.Other, " ") },
        r => r with { Identity = new IdentityVerificationRequest(IdentityVerificationMethod.Other, new string('x', 501)) },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Enroll_RejectsInvalidInput_WithoutTouchingTheAuditTrail(Func<EnrollSignerRequest, EnrollSignerRequest> mutate)
    {
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signers.EnrollAsync(mutate(Harness.EnrollRequest(_h.Tech)), _h.Admin));
        Assert.Empty(_h.Audit.Records);
        Assert.Empty(_h.Data.Signers);
    }

    [Fact]
    public async Task Enroll_Self_IsForbidden_BecauseSigningNeedsTwoPeople()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signers.EnrollAsync(Harness.EnrollRequest(_h.Admin), _h.Admin));
        Assert.Contains("11.200(a)(3)", ex.Message);
    }

    [Fact]
    public async Task Enroll_SameUserTwice_IsAConflict()
    {
        await Enroll();
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.EnrollAsync(Harness.EnrollRequest(_h.Tech, employeeId: "E-other"), _h.Admin));
        Assert.Contains("already enrolled", ex.Message);
        Assert.Single(_h.Audit.OfType(AuditFields.SignerRecordType));
    }

    [Fact]
    public async Task Enroll_SameIdentificationCodeForAnotherUser_IsAConflict()
    {
        await Enroll();
        var request = Harness.EnrollRequest(_h.Reviewer) with { Username = "Tina.Tech" };
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.EnrollAsync(request, _h.Admin));
        Assert.Contains("identification code", ex.Message);
    }

    [Fact]
    public async Task Enroll_SamePersonTwice_IsAConflict_UntilTheFirstEnrollmentIsRevoked()
    {
        await Enroll();
        var second = Harness.EnrollRequest(_h.Reviewer, employeeId: "E-id-tina");
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.EnrollAsync(second, _h.Admin));
        Assert.Contains("live electronic signature", ex.Message);

        await _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("account compromised"), _h.Admin);
        var enrolled = await _h.Signers.EnrollAsync(second, _h.Admin);
        Assert.Equal("id-rob", enrolled.UserId);
    }

    [Fact]
    public async Task Enroll_ARevokedIdentityIsNeverReassigned()
    {
        await Enroll();
        await _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("left the company"), _h.Admin);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.EnrollAsync(Harness.EnrollRequest(_h.Tech, employeeId: "E-new"), _h.Admin));
        Assert.Contains("never reassigned", ex.Message);
    }

    private sealed class RacingStore(ISignerStore inner) : ISignerStore
    {
        public Task<Signer?> FindByUserIdAsync(string userId, CancellationToken ct = default) => Task.FromResult<Signer?>(null);
        public Task<Signer?> FindByUsernameAsync(string username, CancellationToken ct = default) => Task.FromResult<Signer?>(null);
        public Task<bool> EmployeeIdInUseAsync(string employeeId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, DateTime nowUtc, int skip, int take, CancellationToken ct = default) => inner.ListAsync(status, reviewDueOnly, nowUtc, skip, take, ct);
        public Task AddAsync(Signer signer, CancellationToken ct = default) => throw new ConflictException("lost the race");
        public Task UpdateAsync(Signer signer, CancellationToken ct = default) => inner.UpdateAsync(signer, ct);
    }

    [Fact]
    public async Task Enroll_WhenTheSaveLosesARace_VoidsTheAuditRecordAndRethrows()
    {
        var service = new SignerService(new RacingStore(_h.SignerStore), _h.Uow, _h.Audit, _h.Challenge, _h.Auth, _h.Alerts,
            Microsoft.Extensions.Options.Options.Create(_h.Options), _h.Clock, NullLogger<SignerService>.Instance);
        await Assert.ThrowsAsync<ConflictException>(() => service.EnrollAsync(Harness.EnrollRequest(_h.Tech), _h.Admin));
        var audit = Assert.Single(_h.Audit.OfType(AuditFields.SignerRecordType));
        Assert.Equal("NotPersisted", audit.Fields["status"]);
        Assert.Contains("Marked void", audit.Reasons.Last());
    }

    [Fact]
    public async Task Enroll_WhenTheAuditTrailIsDown_EnrollsNobody()
    {
        _h.Audit.FailCreate = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => Enroll());
        Assert.Empty(_h.Data.Signers);
    }

    [Fact]
    public async Task Enroll_WhenVoidingAlsoFails_StillThrowsTheOriginalError()
    {
        var service = new SignerService(new RacingStore(_h.SignerStore), _h.Uow, _h.Audit, _h.Challenge, _h.Auth, _h.Alerts,
            Microsoft.Extensions.Options.Options.Create(_h.Options), _h.Clock, NullLogger<SignerService>.Instance);
        _h.Audit.FailUpdate = true;
        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.EnrollAsync(Harness.EnrollRequest(_h.Tech), _h.Admin));
        Assert.Equal("lost the race", ex.Message);
    }

    // ---- certification (11.100(c)) ----

    [Fact]
    public async Task Certification_ActivatesTheSigner_AfterTheIndividualReentersTheirCredentials()
    {
        await Enroll();
        var signer = await _h.Signers.AcceptCertificationAsync(new CertifyRequest("tina.tech", Harness.Password, "1.0"), _h.Tech);

        Assert.Equal(SignerStatus.Active, signer.Status);
        Assert.Equal(Harness.Start.UtcDateTime, signer.CertificationAcceptedUtc);
        Assert.Equal(2, signer.Version);
        var audit = _h.Audit.OfType(AuditFields.SignerRecordType).Single();
        Assert.Equal("Active", audit.Fields["status"]);
        Assert.Equal("tok-tina", audit.Tokens.Last());
        Assert.Contains("accepted the electronic signature certification", audit.Reasons.Last());
        Assert.Contains(_h.Data.Attempts, a => a.Succeeded);
    }

    [Fact]
    public async Task Certification_RequiresTheCorrectPassword()
    {
        await Enroll();
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => _h.Signers.AcceptCertificationAsync(new CertifyRequest("tina.tech", "wrong", "1.0"), _h.Tech));
        Assert.Equal(SignerStatus.PendingCertification, (await _h.Signers.GetAsync("id-tina")).Status);
    }

    [Fact]
    public async Task Certification_ByAnUnenrolledUser_IsForbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signers.AcceptCertificationAsync(new CertifyRequest("tina.tech", Harness.Password, "1.0"), _h.Tech));
    }

    [Fact]
    public async Task Certification_RequiresTheCurrentStatementVersion()
    {
        await Enroll();
        var ex = await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signers.AcceptCertificationAsync(new CertifyRequest("tina.tech", Harness.Password, "0.9"), _h.Tech));
        Assert.Contains("1.0", ex.Message);
    }

    [Fact]
    public async Task Certification_CannotBeAcceptedTwice()
    {
        await _h.ActivateAsync(_h.Tech);
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.AcceptCertificationAsync(new CertifyRequest("tina.tech", Harness.Password, "1.0"), _h.Tech));
    }

    // ---- loss management (11.300(c)) ----

    [Fact]
    public async Task Suspend_DeauthorizesAtOnce_RevokesSessions_AndAlertsTheSecurityUnit()
    {
        await _h.ActivateAsync(_h.Tech);
        var result = await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost on site"), _h.Admin);

        Assert.Equal(SignerStatus.Suspended, result.Signer.Status);
        Assert.Equal("Badge lost on site", result.Signer.StatusReason);
        Assert.True(result.SessionsRevoked);
        Assert.Equal(["id-tina"], _h.Auth.RevokedUsers);
        var alert = _h.Sink.Alerts.Last();
        Assert.Equal(AlertType.SignerDeauthorized, alert.Type);
        Assert.Contains("suspended by adam.admin", alert.Detail);
        var audit = _h.Audit.OfType(AuditFields.SignerRecordType).Single();
        Assert.Equal("Suspended", audit.Fields["status"]);
        Assert.Contains("Badge lost on site", audit.Reasons.Last());
        Assert.Equal("tok-adam", audit.Tokens.Last());
    }

    [Fact]
    public async Task Suspend_CanSkipSessionRevocation()
    {
        await _h.ActivateAsync(_h.Tech);
        var result = await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Investigating", RevokeSessions: false), _h.Admin);
        Assert.Null(result.SessionsRevoked);
        Assert.Empty(_h.Auth.RevokedUsers);
    }

    [Fact]
    public async Task Suspend_ReportsWhenSessionsCouldNotBeRevoked()
    {
        await _h.ActivateAsync(_h.Tech);
        _h.Auth.RevokeResult = false;
        var result = await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost"), _h.Admin);
        Assert.False(result.SessionsRevoked);
        Assert.Contains("could NOT be revoked", _h.Sink.Alerts.Last().Detail);
        Assert.Equal(SignerStatus.Suspended, result.Signer.Status);
    }

    [Fact]
    public async Task Suspend_StillSucceeds_WhenTheRevokerThrows()
    {
        await _h.ActivateAsync(_h.Tech);
        _h.Auth.RevokeThrows = true;
        var result = await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost"), _h.Admin);
        Assert.False(result.SessionsRevoked);
        Assert.Equal(SignerStatus.Suspended, result.Signer.Status);
    }

    [Fact]
    public async Task Suspend_RequiresAReason_NotYourself_AndAnExistingSigner()
    {
        await _h.ActivateAsync(_h.Tech);
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("abc"), _h.Admin));
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest(new string('x', 1001)), _h.Admin));
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signers.SuspendAsync("id-adam", new SuspendSignerRequest("my own badge"), _h.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signers.SuspendAsync("ghost", new SuspendSignerRequest("who is this"), _h.Admin));
    }

    [Fact]
    public async Task Suspend_RollsBackWhenTheAuditTrailRejectsTheEntry()
    {
        await _h.ActivateAsync(_h.Tech);
        _h.Audit.FailUpdate = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost"), _h.Admin));
        Assert.Equal(SignerStatus.Active, (await _h.Signers.GetAsync("id-tina")).Status);
        Assert.Equal(1, _h.Uow.Rollbacks);
    }

    [Fact]
    public async Task Reinstate_RequiresFreshIdentityVerification_AndNewReviewDate()
    {
        await _h.ActivateAsync(_h.Tech);
        await _h.Signers.SuspendAsync("id-tina", new SuspendSignerRequest("Badge lost"), _h.Admin);
        _h.Clock.Advance(TimeSpan.FromDays(2));

        var result = await _h.Signers.ReinstateAsync("id-tina",
            new ReinstateSignerRequest("Badge found; identity re-checked", new IdentityVerificationRequest(IdentityVerificationMethod.EmployeeBadgeInPerson, "Badge sighted by security")), _h.Admin);

        Assert.Equal(SignerStatus.Active, result.Signer.Status);
        Assert.Null(result.SessionsRevoked);
        Assert.Equal(IdentityVerificationMethod.EmployeeBadgeInPerson, result.Signer.Identity.Method);
        Assert.Equal(Harness.Start.UtcDateTime.AddDays(2).AddMonths(12), result.Signer.NextReviewDueUtc);
    }

    [Fact]
    public async Task Reinstate_OnlyApplies_ToSuspendedSigners()
    {
        await _h.ActivateAsync(_h.Tech);
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.ReinstateAsync("id-tina", new ReinstateSignerRequest("not suspended", Harness.Identity), _h.Admin));
    }

    [Fact]
    public async Task Revoke_IsPermanent_AndRecorded()
    {
        await _h.ActivateAsync(_h.Tech);
        var result = await _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("Credentials confirmed stolen"), _h.Admin);
        Assert.Equal(SignerStatus.Revoked, result.Signer.Status);
        Assert.True(result.SessionsRevoked);
        Assert.Contains("revoked by adam.admin", _h.Sink.Alerts.Last().Detail);
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("again please"), _h.Admin));
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.ReinstateAsync("id-tina", new ReinstateSignerRequest("try to undo", Harness.Identity), _h.Admin));
    }

    [Fact]
    public async Task Revoke_CanSkipSessionRevocation()
    {
        await _h.ActivateAsync(_h.Tech);
        var result = await _h.Signers.RevokeAsync("id-tina", new RevokeSignerRequest("Left the company", RevokeSessions: false), _h.Admin);
        Assert.Null(result.SessionsRevoked);
    }

    // ---- periodic review (11.300(b)) ----

    [Fact]
    public async Task Review_ExtendsSigningAuthority_AfterReverifyingTheIndividual()
    {
        await _h.ActivateAsync(_h.Tech);
        _h.Clock.Advance(TimeSpan.FromDays(300));
        var result = await _h.Signers.ReviewAsync("id-tina", new ReviewSignerRequest("Annual recertification", Harness.Identity), _h.Admin);
        var expectedNow = Harness.Start.UtcDateTime.AddDays(300);
        Assert.Equal(expectedNow, result.Signer.LastReviewedUtc);
        Assert.Equal(expectedNow.AddMonths(12), result.Signer.NextReviewDueUtc);
        Assert.Contains("Periodic review completed", _h.Audit.OfType(AuditFields.SignerRecordType).Single().Reasons.Last());
    }

    [Fact]
    public async Task Review_RequiresAnActiveSigner_AndSomeoneElse()
    {
        await Enroll();
        await Assert.ThrowsAsync<ConflictException>(() => _h.Signers.ReviewAsync("id-tina", new ReviewSignerRequest("not certified yet", Harness.Identity), _h.Admin));
        await Assert.ThrowsAsync<ForbiddenException>(() => _h.Signers.ReviewAsync("id-adam", new ReviewSignerRequest("reviewing myself", Harness.Identity), _h.Admin));
    }

    // ---- reading ----

    [Fact]
    public async Task Get_ThrowsNotFound_ForAnUnknownUser()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signers.GetAsync("ghost"));
    }

    [Fact]
    public async Task List_FiltersByStatusAndReviewDue()
    {
        await _h.ActivateAsync(_h.Tech);
        await _h.ActivateAsync(_h.Reviewer);
        await _h.Signers.SuspendAsync("id-rob", new SuspendSignerRequest("Badge lost"), _h.Admin);
        await _h.Signers.EnrollAsync(Harness.EnrollRequest(_h.Manager), _h.Admin);

        Assert.Equal(3, (await _h.Signers.ListAsync(null, false, 0, 0)).Count);
        Assert.Equal(["id-tina"], (await _h.Signers.ListAsync(SignerStatus.Active, false, 0, 0)).Select(s => s.UserId));
        Assert.Equal(["id-rob"], (await _h.Signers.ListAsync(SignerStatus.Suspended, false, 0, 0)).Select(s => s.UserId));
        Assert.Empty(await _h.Signers.ListAsync(null, true, 0, 0));

        _h.Clock.Advance(TimeSpan.FromDays(400));
        Assert.Equal(3, (await _h.Signers.ListAsync(null, true, 0, 0)).Count);
        Assert.Single(await _h.Signers.ListAsync(null, false, 1, 1));
    }

    [Fact]
    public async Task Certification_Text_NamesTheSignerAndOrganization()
    {
        await _h.ActivateAsync(_h.Tech, "Tina Technician");
        var text = await _h.Signers.GetCertificationAsync("id-tina");
        Assert.Contains("Tina Technician", text);
        Assert.Contains("Acme Pharma", text);
        Assert.Contains("Accepted (UTC)", text);
        await Assert.ThrowsAsync<NotFoundException>(() => _h.Signers.GetCertificationAsync("ghost"));
    }
}

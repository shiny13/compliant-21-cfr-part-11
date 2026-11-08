using AuthenticationCore.Shared.Roles;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Tests;

public class ModelTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IdentityVerification Verified(DateTime? at = null) =>
        new(IdentityVerificationMethod.EmployeeBadgeInPerson, "badge 42", "adm", "adam", at ?? T0);

    private static Signer NewSigner() => new()
    {
        Id = Guid.NewGuid(), UserId = "u1", Username = "tina", PrintedName = "Tina Tech", EmployeeId = "E1",
        Status = SignerStatus.PendingCertification, StatusReason = "Enrolled", StatusChangedUtc = T0, EnrolledUtc = T0,
        EnrolledByUserId = "adm", EnrolledByUsername = "adam", Identity = Verified(), LastReviewedUtc = T0, NextReviewDueUtc = T0.AddMonths(12),
    };

    private static Signer Active()
    {
        var s = NewSigner();
        s.AcceptCertification("1.0", T0);
        return s;
    }

    [Fact]
    public void Operator_ToStringNeverRevealsTheToken()
    {
        var op = new Operator("u1", "tina", UserRole.Reviewer, "secret-token");
        Assert.DoesNotContain("secret-token", op.ToString());
        Assert.Contains("tina", op.ToString());
        Assert.Equal("secret-token", op.AccessToken);
    }

    [Fact]
    public void PendingSigner_CannotSignUntilCertified()
    {
        var s = NewSigner();
        var ex = Assert.Throws<ForbiddenException>(() => s.EnsureCanSign(T0));
        Assert.Contains("11.100(c)", ex.Message);
    }

    [Fact]
    public void AcceptCertification_ActivatesAndRecordsVersionAndTime()
    {
        var s = NewSigner();
        s.AcceptCertification("1.0", T0.AddHours(1));
        Assert.Equal(SignerStatus.Active, s.Status);
        Assert.Equal("1.0", s.CertificationVersion);
        Assert.Equal(T0.AddHours(1), s.CertificationAcceptedUtc);
        s.EnsureCanSign(T0.AddHours(2));
    }

    [Fact]
    public void AcceptCertification_TwiceIsAConflict()
    {
        var s = Active();
        Assert.Contains("already been accepted", Assert.Throws<ConflictException>(() => s.AcceptCertification("1.0", T0)).Message);
    }

    [Fact]
    public void AcceptCertification_WhenSuspendedOrRevokedIsAConflict()
    {
        var s = NewSigner();
        s.Suspend("lost badge", T0);
        Assert.Contains("Suspended", Assert.Throws<ConflictException>(() => s.AcceptCertification("1.0", T0)).Message);
    }

    [Fact]
    public void SuspendedAndRevokedSignersCannotSign()
    {
        var s = Active();
        s.Suspend("credentials possibly stolen", T0);
        Assert.Contains("suspended", Assert.Throws<ForbiddenException>(() => s.EnsureCanSign(T0)).Message);
        s.Revoke("confirmed compromise", T0);
        Assert.Contains("revoked", Assert.Throws<ForbiddenException>(() => s.EnsureCanSign(T0)).Message);
    }

    [Fact]
    public void OverdueReview_BlocksSigningUntilReviewed()
    {
        var s = Active();
        s.EnsureCanSign(T0.AddMonths(12).AddTicks(-1));
        var due = T0.AddMonths(12);
        Assert.True(s.IsReviewOverdue(due));
        Assert.Contains("overdue", Assert.Throws<ForbiddenException>(() => s.EnsureCanSign(due)).Message);

        s.Review("annual check", Verified(due), due, 12);
        s.EnsureCanSign(due.AddDays(1));
        Assert.Equal(due.AddMonths(12), s.NextReviewDueUtc);
        Assert.Equal(due, s.LastReviewedUtc);
        Assert.Equal("annual check", s.StatusReason);
    }

    [Fact]
    public void Suspend_OnlyFromPendingOrActive()
    {
        var s = NewSigner();
        s.Suspend("first", T0);
        Assert.Contains("pending or active", Assert.Throws<ConflictException>(() => s.Suspend("again", T0)).Message);
        var r = Active();
        r.Revoke("gone", T0);
        Assert.Throws<ConflictException>(() => r.Suspend("again", T0));
    }

    [Fact]
    public void Reinstate_ReturnsToActive_WhenAlreadyCertified()
    {
        var s = Active();
        s.Suspend("lost", T0);
        var later = T0.AddDays(3);
        s.Reinstate("found, re-verified", Verified(later), later, 12);
        Assert.Equal(SignerStatus.Active, s.Status);
        Assert.Equal(later.AddMonths(12), s.NextReviewDueUtc);
        Assert.Equal(later, s.Identity.VerifiedUtc);
    }

    [Fact]
    public void Reinstate_ReturnsToPending_WhenNeverCertified()
    {
        var s = NewSigner();
        s.Suspend("hold", T0);
        s.Reinstate("ok", Verified(), T0, 12);
        Assert.Equal(SignerStatus.PendingCertification, s.Status);
    }

    [Fact]
    public void Reinstate_RequiresSuspended()
    {
        var s = Active();
        Assert.Contains("suspended signer", Assert.Throws<ConflictException>(() => s.Reinstate("x", Verified(), T0, 12)).Message);
    }

    [Fact]
    public void Revoke_IsTerminal()
    {
        var s = Active();
        s.Revoke("left company", T0);
        Assert.Equal(SignerStatus.Revoked, s.Status);
        Assert.Contains("already revoked", Assert.Throws<ConflictException>(() => s.Revoke("again", T0)).Message);
        Assert.Throws<ConflictException>(() => s.Reinstate("x", Verified(), T0, 12));
        Assert.Throws<ConflictException>(() => s.Review("x", Verified(), T0, 12));
    }

    [Fact]
    public void Review_RequiresActive()
    {
        var s = NewSigner();
        Assert.Contains("active signer", Assert.Throws<ConflictException>(() => s.Review("x", Verified(), T0, 12)).Message);
    }

    [Fact]
    public void MeaningCatalog_ContainsTheFourPart1150Meanings()
    {
        Assert.Equal(["Authorship", "Responsibility", "Review", "Approval"], MeaningCatalog.All.Select(m => m.Code));
        Assert.All(MeaningCatalog.All, m => Assert.False(string.IsNullOrWhiteSpace(m.Statement)));
    }

    [Theory]
    [InlineData("review", "Review")]
    [InlineData(" APPROVAL ", "Approval")]
    [InlineData("Authorship", "Authorship")]
    public void MeaningCatalog_FindIsCaseInsensitive(string input, string expected) =>
        Assert.Equal(expected, MeaningCatalog.Find(input)?.Code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Witness")]
    public void MeaningCatalog_UnknownIsNull(string? input) => Assert.Null(MeaningCatalog.Find(input));

    [Fact]
    public void MeaningCatalog_RolesRiseWithResponsibility()
    {
        Assert.Equal(UserRole.LabTechnician, MeaningCatalog.Find("Authorship")!.MinimumRole);
        Assert.Equal(UserRole.Reviewer, MeaningCatalog.Find("Review")!.MinimumRole);
        Assert.Equal(UserRole.LabManager, MeaningCatalog.Find("Approval")!.MinimumRole);
    }

    [Fact]
    public void CertificationStatement_NamesTheOrganizationAndLegalEquivalence()
    {
        var text = CertificationStatement.Statement("Acme");
        Assert.Contains("Acme", text);
        Assert.Contains("legally binding equivalent of my traditional handwritten signature", text);
    }

    [Fact]
    public void CertificationRender_ShowsAcceptedAndPendingStates()
    {
        var pending = CertificationStatement.Render(NewSigner(), "Acme");
        Assert.Contains("Tina Tech", pending);
        Assert.Contains("NOT YET ACCEPTED", pending);
        Assert.Contains("badge 42", pending);

        var accepted = CertificationStatement.Render(Active(), "Acme");
        Assert.Contains("Accepted (UTC)", accepted);
        Assert.Contains("2026-03-01 12:00:00 UTC", accepted);
        Assert.DoesNotContain("NOT YET", accepted);
    }

    [Fact]
    public void AttemptSucceeded_ReflectsOutcome()
    {
        Assert.True(new SigningAttempt(Guid.NewGuid(), "u", "n", T0, AttemptOutcome.Success, null).Succeeded);
        Assert.False(new SigningAttempt(Guid.NewGuid(), "u", "n", T0, AttemptOutcome.BadCredentials, null).Succeeded);
    }

    [Fact]
    public void ValueRecords_CompareByValue()
    {
        var rev = new SignatureRevocation(Guid.NewGuid(), T0, "u", "n", "why");
        Assert.Equal(rev, rev with { });
        Assert.NotEqual(rev, rev with { Reason = "other" });
        Assert.Equal(rev.GetHashCode(), (rev with { }).GetHashCode());
        Assert.Contains("why", rev.ToString());

        var attempt = new SigningAttempt(Guid.NewGuid(), "u", "n", T0, AttemptOutcome.Success, null);
        Assert.Equal(attempt, attempt with { });
        Assert.NotEqual(attempt, attempt with { Outcome = AttemptOutcome.BadCredentials });
        Assert.Equal(attempt.GetHashCode(), (attempt with { }).GetHashCode());
        Assert.Contains("Success", attempt.ToString());
    }
}

using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

public class CredentialChallengeTests
{
    private readonly Harness _h = new();

    private Task Verify(string? username = null, string? password = null) =>
        _h.Challenge.VerifyAsync(_h.Tech, username ?? _h.Tech.Username, password ?? Harness.Password, null);

    [Theory]
    [InlineData("", "x")]
    [InlineData("  ", "x")]
    [InlineData("tina.tech", "")]
    [InlineData(null, "x")]
    [InlineData("tina.tech", null)]
    public async Task BlankComponents_AreRejectedWithoutBeingCounted(string? user, string? pass)
    {
        await Assert.ThrowsAsync<SignatureValidationException>(() => _h.Challenge.VerifyAsync(_h.Tech, user, pass, null));
        Assert.Empty(_h.Data.Attempts);
        Assert.Equal(0, _h.Auth.Calls);
    }

    [Fact]
    public async Task CorrectCredentials_Pass_AndAreLogged()
    {
        await Verify();
        var attempt = Assert.Single(_h.Data.Attempts);
        Assert.Equal(AttemptOutcome.Success, attempt.Outcome);
        Assert.Equal("id-tina", attempt.UserId);
        Assert.Empty(_h.Data.Alerts);
    }

    [Fact]
    public async Task UsernameIsMatchedIgnoringCaseAndPadding()
    {
        await Verify("  TINA.Tech ");
        Assert.Equal(1, _h.Auth.Calls);
    }

    [Fact]
    public async Task VerifierIsAskedAboutTheSessionOwner_NotWhatWasTyped()
    {
        await Verify("TINA.TECH");
        Assert.Equal("tina.tech", _h.Auth.LastUsername);
    }

    [Fact]
    public async Task AnotherPersonsIdentificationCode_IsACriticalAlert_AndNeverReachesTheIdentityProvider()
    {
        var ex = await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify("rob.reviewer", Harness.Password));
        Assert.Equal(CredentialChallenge.GenericFailure, ex.Message);
        Assert.Equal(0, _h.Auth.Calls);
        Assert.Equal(AttemptOutcome.UsernameMismatch, Assert.Single(_h.Data.Attempts).Outcome);
        var alert = Assert.Single(_h.Sink.Alerts);
        Assert.Equal(AlertType.IdentityMismatch, alert.Type);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.Equal("id-tina", alert.UserId);
        Assert.Contains("rob.reviewer", alert.Detail);
    }

    [Fact]
    public async Task LongTypedCodesAreTruncatedInAlerts()
    {
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(new string('x', 500)));
        Assert.True(_h.Sink.Alerts.Single().Detail.Length < 300);
    }

    [Fact]
    public async Task WrongPassword_IsGeneric_Logged_AndWarned()
    {
        var ex = await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        Assert.Equal(CredentialChallenge.GenericFailure, ex.Message);
        Assert.Equal(AttemptOutcome.BadCredentials, Assert.Single(_h.Data.Attempts).Outcome);
        var alert = Assert.Single(_h.Sink.Alerts);
        Assert.Equal(AlertType.FailedSignatureAuthentication, alert.Type);
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.Contains("1 of 3", alert.Detail);
    }

    [Fact]
    public async Task ThreeFailures_LockSigning_EvenForTheCorrectPassword()
    {
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        }
        Assert.Equal(AlertType.SignerLockedOut, _h.Sink.Alerts.Last().Type);
        Assert.Equal(AlertSeverity.Critical, _h.Sink.Alerts.Last().Severity);

        var callsBefore = _h.Auth.Calls;
        var locked = await Assert.ThrowsAsync<SignerLockedException>(() => Verify());
        Assert.Equal(callsBefore, _h.Auth.Calls);
        Assert.Equal(Harness.Start.UtcDateTime + TimeSpan.FromMinutes(15), locked.LockedUntilUtc);
        Assert.Equal(AlertType.AttemptWhileLocked, _h.Sink.Alerts.Last().Type);
        Assert.Equal(3, _h.Data.Attempts.Count);
    }

    [Fact]
    public async Task LockExpires_AfterTheLockoutDuration()
    {
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        }
        _h.Clock.Advance(TimeSpan.FromMinutes(14));
        await Assert.ThrowsAsync<SignerLockedException>(() => Verify());
        _h.Clock.Advance(TimeSpan.FromMinutes(2));
        await Verify();
    }

    [Fact]
    public async Task SuccessResetsTheFailureCount()
    {
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        }
        await Verify();
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        }
        await Verify();
    }

    [Fact]
    public async Task OldFailuresOutsideTheWindowDoNotCount()
    {
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        Assert.Contains("2 of 3", _h.Sink.Alerts.Last().Detail);
        await Verify();
    }

    [Fact]
    public async Task LockoutLimitIsConfigurable()
    {
        var h = new Harness(o => o.MaxFailedAttempts = 1);
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => h.Challenge.VerifyAsync(h.Tech, "tina.tech", "nope", null));
        await Assert.ThrowsAsync<SignerLockedException>(() => h.Challenge.VerifyAsync(h.Tech, "tina.tech", Harness.Password, null));
    }

    [Fact]
    public async Task OneUsersFailuresDoNotLockAnother()
    {
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify(password: "nope"));
        }
        await _h.Challenge.VerifyAsync(_h.Reviewer, _h.Reviewer.Username, Harness.Password, null);
    }

    [Fact]
    public async Task CredentialsOfADifferentAccount_AreACriticalAlert()
    {
        _h.Auth.Add("tina.tech", Harness.Password, "someone-else");
        await Assert.ThrowsAsync<SignatureAuthenticationException>(() => Verify());
        Assert.Equal(AttemptOutcome.SubjectMismatch, Assert.Single(_h.Data.Attempts).Outcome);
        Assert.Equal(AlertType.IdentityMismatch, Assert.Single(_h.Sink.Alerts).Type);
    }

    [Fact]
    public async Task IdentityProviderOutage_FailsClosed_WithoutCountingAnAttempt()
    {
        _h.Auth.Unavailable = true;
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => Verify());
        Assert.Empty(_h.Data.Attempts);
    }

    [Fact]
    public void Evaluate_WithNoHistory_IsNotLocked()
    {
        var (failures, until) = CredentialChallenge.Evaluate([], Harness.Start.UtcDateTime, new SignatureOptions());
        Assert.Equal(0, failures);
        Assert.Null(until);
    }
}

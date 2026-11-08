using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

public interface ICredentialChallenge
{
    /// <summary>Proves the caller knows both signature components. Throws unless the individual is verified.</summary>
    Task VerifyAsync(Operator caller, string? username, string? password, Guid? recordId, CancellationToken ct = default);
}

/// <summary>
/// Executes the identification-code + password check that precedes every signing and certification (11.200(a)) with the
/// transaction safeguards of 11.300(d): every attempt is logged, repeated failures lock the individual out, and any
/// sign of someone else's credentials being used raises an immediate alert.
/// </summary>
public sealed class CredentialChallenge(
    ICredentialVerifier verifier,
    ISecurityStore store,
    ISecurityAlertService alerts,
    IOptions<SignatureOptions> options,
    TimeProvider clock) : ICredentialChallenge
{
    public const string GenericFailure = "The identification code or password was not accepted.";

    public async Task VerifyAsync(Operator caller, string? username, string? password, Guid? recordId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new SignatureValidationException("Both the identification code and the password are required.");
        }

        var settings = options.Value;
        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());

        // The code typed must be the logged-in individual's own: a signature can never be applied "as" someone else.
        if (!string.Equals(username.Trim(), caller.Username, StringComparison.OrdinalIgnoreCase))
        {
            await RecordAsync(caller, AttemptOutcome.UsernameMismatch, recordId, now, ct);
            await alerts.RaiseAsync(AlertType.IdentityMismatch, AlertSeverity.Critical, caller.UserId, caller.Username,
                $"A signing was attempted with identification code '{Truncate(username)}' from the session of '{caller.Username}'.", caller, ct);
            throw new SignatureAuthenticationException(GenericFailure);
        }

        var history = await store.GetAttemptsSinceAsync(caller.UserId, now - settings.FailureWindow - settings.LockoutDuration, ct);
        var state = Evaluate(history, now, settings);
        if (state.LockedUntilUtc is { } until)
        {
            await alerts.RaiseAsync(AlertType.AttemptWhileLocked, AlertSeverity.Warning, caller.UserId, caller.Username,
                "A signing was attempted while the individual's signing was locked.", caller, ct);
            throw new SignerLockedException(until);
        }

        var result = await verifier.VerifyAsync(caller.Username, password, ct);

        if (!result.Verified)
        {
            var attempt = await RecordAsync(caller, AttemptOutcome.BadCredentials, recordId, now, ct);
            var failures = Evaluate(history.Append(attempt), now, settings).Failures;
            if (failures >= settings.MaxFailedAttempts)
            {
                await alerts.RaiseAsync(AlertType.SignerLockedOut, AlertSeverity.Critical, caller.UserId, caller.Username,
                    $"{failures} consecutive failed signature authentications; signing is locked for {settings.LockoutDuration.TotalMinutes:0} minutes.", caller, ct);
            }
            else
            {
                await alerts.RaiseAsync(AlertType.FailedSignatureAuthentication, AlertSeverity.Warning, caller.UserId, caller.Username,
                    $"Failed signature authentication ({failures} of {settings.MaxFailedAttempts} before lockout).", caller, ct);
            }
            throw new SignatureAuthenticationException(GenericFailure);
        }

        if (!string.Equals(result.Subject, caller.UserId, StringComparison.Ordinal))
        {
            await RecordAsync(caller, AttemptOutcome.SubjectMismatch, recordId, now, ct);
            await alerts.RaiseAsync(AlertType.IdentityMismatch, AlertSeverity.Critical, caller.UserId, caller.Username,
                "The credentials verified belong to a different account than the logged-in session.", caller, ct);
            throw new SignatureAuthenticationException(GenericFailure);
        }

        await RecordAsync(caller, AttemptOutcome.Success, recordId, now, ct);
    }

    /// <summary>
    /// Counts the current streak of failures (those after the last success and within the failure window of the latest
    /// failure) and says whether signing is locked.
    /// </summary>
    internal static (int Failures, DateTime? LockedUntilUtc) Evaluate(IEnumerable<SigningAttempt> attempts, DateTime nowUtc, SignatureOptions settings)
    {
        var ordered = attempts.OrderBy(a => a.AttemptedUtc).ToList();
        var lastSuccess = ordered.LastOrDefault(a => a.Succeeded)?.AttemptedUtc;
        var failures = ordered.Where(a => !a.Succeeded && (lastSuccess is null || a.AttemptedUtc > lastSuccess)).ToList();
        if (failures.Count == 0)
        {
            return (0, null);
        }

        var lastFailure = failures[^1].AttemptedUtc;
        var streak = failures.Count(a => a.AttemptedUtc >= lastFailure - settings.FailureWindow);
        var until = lastFailure + settings.LockoutDuration;
        return streak >= settings.MaxFailedAttempts && nowUtc < until ? (streak, until) : (streak, null);
    }

    private async Task<SigningAttempt> RecordAsync(Operator caller, AttemptOutcome outcome, Guid? recordId, DateTime now, CancellationToken ct)
    {
        var attempt = new SigningAttempt(Guid.NewGuid(), caller.UserId, caller.Username, now, outcome, recordId);
        await store.AddAttemptAsync(attempt, ct);
        return attempt;
    }

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64];
}

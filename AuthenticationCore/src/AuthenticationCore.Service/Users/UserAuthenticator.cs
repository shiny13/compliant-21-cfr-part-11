using AuthenticationCore.Service.Auditing;
using AuthenticationCore.Service.Options;
using AuthenticationCore.Service.Security;
using Microsoft.Extensions.Options;

namespace AuthenticationCore.Service.Users;

public enum AuthFailure
{
    None,
    UnknownUser,
    BadPassword,
    Locked,
    Inactive,
}

public sealed record AuthenticationResult(UserAccount? User, AuthFailure Failure)
{
    public bool Succeeded => User is not null;
}

public sealed class UserAuthenticator(
    IUserStore store,
    PasswordHasher hasher,
    IAuditLog audit,
    IOptions<AuthServiceOptions> options,
    TimeProvider clock)
{
    // Verified against when the username is unknown so response time doesn't reveal which usernames exist.
    private readonly Lazy<string> _decoyHash = new(() => hasher.Hash("decoy-password"));

    public async ValueTask<AuthenticationResult> AuthenticateAsync(string username, string password)
    {
        var settings = options.Value;
        var user = await store.FindByUsernameAsync(username);

        if (user is null)
        {
            hasher.Verify(password, _decoyHash.Value);
            audit.Record("login", "failure", detail: "unknown user");
            return new AuthenticationResult(null, AuthFailure.UnknownUser);
        }

        lock (user.Sync)
        {
            if (user.LockedUntil is { } until && until > clock.GetUtcNow())
            {
                audit.Record("login", "failure", user.Id.ToString(), "account locked");
                return new AuthenticationResult(null, AuthFailure.Locked);
            }
        }

        var passwordOk = hasher.Verify(password, user.PasswordHash);

        lock (user.Sync)
        {
            if (!passwordOk)
            {
                user.FailedAttempts++;
                if (user.FailedAttempts >= settings.MaxFailedAttempts)
                {
                    user.FailedAttempts = 0;
                    user.LockedUntil = clock.GetUtcNow() + settings.LockoutDuration;
                    audit.Record("account-lockout", "success", user.Id.ToString(), "too many failed attempts");
                }
                audit.Record("login", "failure", user.Id.ToString(), "bad password");
                return new AuthenticationResult(null, AuthFailure.BadPassword);
            }

            if (!user.IsActive)
            {
                audit.Record("login", "failure", user.Id.ToString(), "inactive account");
                return new AuthenticationResult(null, AuthFailure.Inactive);
            }

            user.FailedAttempts = 0;
            user.LockedUntil = null;
        }

        audit.Record("login", "success", user.Id.ToString());
        return new AuthenticationResult(user, AuthFailure.None);
    }
}

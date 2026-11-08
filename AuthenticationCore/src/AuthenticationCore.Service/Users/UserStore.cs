using System.Collections.Concurrent;
using AuthenticationCore.Shared.Roles;

namespace AuthenticationCore.Service.Users;

public sealed class UserAccount(Guid id, string username, string passwordHash, IReadOnlyList<UserRole> roles)
{
    public Guid Id { get; } = id;
    public string Username { get; } = username;
    public string PasswordHash { get; } = passwordHash;
    public IReadOnlyList<UserRole> Roles { get; } = roles;
    public bool IsActive { get; set; } = true;

    public object Sync { get; } = new();
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>Persistence seam. Swap the in-memory implementation for a durable store in real deployments.</summary>
public interface IUserStore
{
    ValueTask<UserAccount?> FindByUsernameAsync(string username);

    ValueTask<UserAccount?> FindByIdAsync(Guid id);

    ValueTask AddAsync(UserAccount user);
}

public sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<Guid, UserAccount> _byId = new();
    private readonly ConcurrentDictionary<string, UserAccount> _byUsername = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<UserAccount?> FindByUsernameAsync(string username) =>
        ValueTask.FromResult(_byUsername.GetValueOrDefault(username));

    public ValueTask<UserAccount?> FindByIdAsync(Guid id) =>
        ValueTask.FromResult(_byId.GetValueOrDefault(id));

    public ValueTask AddAsync(UserAccount user)
    {
        if (!_byUsername.TryAdd(user.Username, user))
        {
            throw new InvalidOperationException($"User '{user.Username}' already exists.");
        }
        _byId[user.Id] = user;
        return ValueTask.CompletedTask;
    }
}

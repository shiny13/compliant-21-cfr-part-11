using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.TestSupport;

public static class TestOptions
{
    public static readonly string Key1 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    public static readonly string Key2 = Convert.ToBase64String(Enumerable.Repeat((byte)9, 48).ToArray());

    public static SignatureOptions Create(Action<SignatureOptions>? configure = null)
    {
        var options = new SignatureOptions
        {
            SealKeys = new Dictionary<string, string> { ["k1"] = Key1 },
            ActiveSealKeyId = "k1",
            AuthServiceBaseUrl = "http://auth.test",
            AuditTrailBaseUrl = "http://audit.test",
            OrganizationName = "Acme Pharma",
        };
        configure?.Invoke(options);
        return options;
    }

    public static IOptions<SignatureOptions> Wrap(Action<SignatureOptions>? configure = null) => Options.Create(Create(configure));
}

/// <summary>A scriptable stand-in for RecordAuditTrails that behaves like it: versions, replace-all fields, reasons.</summary>
public sealed class FakeAuditTrail : IAuditTrailClient
{
    public sealed class Entry(Guid id, string type, string title, Dictionary<string, string?> fields)
    {
        public Guid Id { get; } = id;
        public string Type { get; } = type;
        public string Title { get; set; } = title;
        public Dictionary<string, string?> Fields { get; set; } = fields;
        public int Version { get; set; } = 1;
        public bool IsDeleted { get; set; }
        public List<string> Reasons { get; } = [];
        public List<string> Tokens { get; } = [];
    }

    public Dictionary<Guid, Entry> Records { get; } = [];

    public bool FailCreate { get; set; }

    public bool UnexpectedFailure { get; set; }

    public bool FailUpdate { get; set; }

    public bool FailUpdateOnce { get; set; }

    public Guid AddRecord(string type = "BatchRecord", string title = "Batch 1", Dictionary<string, string?>? fields = null)
    {
        var entry = new Entry(Guid.NewGuid(), type, title, fields ?? new Dictionary<string, string?> { ["pH"] = "7.0" });
        Records[entry.Id] = entry;
        return entry.Id;
    }

    public void Modify(Guid id, string field = "pH", string? value = "7.5")
    {
        var entry = Records[id];
        entry.Fields[field] = value;
        entry.Version++;
    }

    public IEnumerable<Entry> OfType(string type) => Records.Values.Where(r => r.Type == type);

    public Task<RecordSnapshot> GetRecordAsync(Guid recordId, string accessToken, CancellationToken ct = default)
    {
        if (UnexpectedFailure)
        {
            throw new InvalidOperationException("secret internal detail");
        }
        if (!Records.TryGetValue(recordId, out var e))
        {
            throw new NotFoundException($"Record {recordId} was not found.");
        }
        return Task.FromResult(new RecordSnapshot(e.Id, e.Type, e.Title, new Dictionary<string, string?>(e.Fields), e.Version, e.IsDeleted));
    }

    public Task<Guid> CreateRecordAsync(string recordType, string title, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default)
    {
        if (FailCreate)
        {
            throw new DependencyUnavailableException("audit down");
        }
        var entry = new Entry(Guid.NewGuid(), recordType, title, new Dictionary<string, string?>(fields));
        entry.Reasons.Add(reason);
        entry.Tokens.Add(accessToken);
        Records[entry.Id] = entry;
        return Task.FromResult(entry.Id);
    }

    public Task UpdateRecordAsync(Guid recordId, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default)
    {
        if (FailUpdateOnce)
        {
            FailUpdateOnce = false;
            throw new DependencyUnavailableException("audit down");
        }
        if (FailUpdate)
        {
            throw new DependencyUnavailableException("audit down");
        }
        var entry = Records[recordId];
        entry.Fields = new Dictionary<string, string?>(fields);
        entry.Version++;
        entry.Reasons.Add(reason);
        entry.Tokens.Add(accessToken);
        return Task.CompletedTask;
    }
}

public sealed class FakeCredentialVerifier : ICredentialVerifier, ISessionRevoker
{
    private readonly Dictionary<string, (string Password, string Subject)> _users = new(StringComparer.OrdinalIgnoreCase);

    public bool Unavailable { get; set; }

    public int Calls { get; private set; }

    public string? LastUsername { get; private set; }

    public bool RevokeResult { get; set; } = true;

    public bool RevokeThrows { get; set; }

    public List<string> RevokedUsers { get; } = [];

    public void Add(string username, string password, string subject) => _users[username] = (password, subject);

    public Task<CredentialResult> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        Calls++;
        LastUsername = username;
        if (Unavailable)
        {
            throw new DependencyUnavailableException("auth down");
        }
        return Task.FromResult(_users.TryGetValue(username, out var u) && u.Password == password
            ? CredentialResult.Accepted(u.Subject)
            : CredentialResult.Rejected);
    }

    public Task<bool> RevokeUserSessionsAsync(string userId, string administratorAccessToken, CancellationToken ct = default)
    {
        if (RevokeThrows)
        {
            throw new InvalidOperationException("boom");
        }
        RevokedUsers.Add(userId);
        return Task.FromResult(RevokeResult);
    }
}

public sealed class CollectingAlertSink : ISecurityAlertSink
{
    public List<SecurityAlert> Alerts { get; } = [];

    public Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default)
    {
        Alerts.Add(alert);
        return Task.CompletedTask;
    }
}

public sealed class ThrowingAlertSink : ISecurityAlertSink
{
    public Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default) =>
        throw new InvalidOperationException("sink failure");
}

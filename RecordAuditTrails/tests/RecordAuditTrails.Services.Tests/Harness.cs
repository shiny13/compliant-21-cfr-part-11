using AuthenticationCore.Shared.Roles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Core.Retention;
using RecordAuditTrails.Database;
using RecordAuditTrails.Services.Auditing;
using RecordAuditTrails.Services.Documents;
using RecordAuditTrails.Services.Records;
using RecordAuditTrails.Services.Retention;

namespace RecordAuditTrails.Services.Tests;

/// <summary>Real services and repositories over an in-memory SQLite database and a controllable clock.</summary>
public sealed class Harness : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public static readonly Actor Tech = new("u-tech", "tina.tech", UserRole.LabTechnician);
    public static readonly Actor Reviewer = new("u-rev", "rob.reviewer", UserRole.Reviewer);
    public static readonly Actor Manager = new("u-mgr", "mia.manager", UserRole.LabManager);
    public static readonly Actor Admin = new("u-adm", "adam.admin", UserRole.Administrator);

    public TestClock Clock { get; } = new();

    public AuditDbContext Db { get; }

    public IRecordService Records { get; }

    public IAuditTrailService Trail { get; }

    public IRecordExportService Export { get; }

    public IRetentionService Retention { get; }

    public ISystemDocumentService Documents { get; }

    public Harness(Action<RetentionOptions>? configureRetention = null)
    {
        _connection.Open();
        Db = NewContext();
        Db.Database.EnsureCreated();

        var options = new RetentionOptions();
        configureRetention?.Invoke(options);

        var records = new RecordRepository(Db);
        var trail = new AuditTrailRepository(Db);
        var log = new AccessLogRepository(Db);
        var documents = new DocumentRepository(Db);
        var uow = new UnitOfWork(Db);

        Records = new RecordService(records, new AuditTrailWriter(trail), uow, new RetentionPolicy(options), Clock);
        Trail = new AuditTrailService(trail, records);
        Export = new RecordExportService(records, trail, log, uow, ReportExporter.CreateDefault(), Clock);
        Retention = new RetentionService(records, Clock);
        Documents = new SystemDocumentService(documents, log, uow, Clock);
    }

    public AuditDbContext NewContext() => new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseSqlite(_connection)
        .AddInterceptors(new ImmutabilityInterceptor())
        .Options);

    /// <summary>Bypasses the application (as a malicious DBA would) to prove tampering is detected.</summary>
    public Task TamperAsync(string sql) => Db.Database.ExecuteSqlRawAsync(sql);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }

    public static CreateRecordRequest NewRecord(string type = "BatchRecord", string title = "Batch 1") =>
        new(type, title, new Dictionary<string, string?> { ["pH"] = "7.0", ["operator"] = "tina" }, null);
}

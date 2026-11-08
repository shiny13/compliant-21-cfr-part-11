using AuthenticationCore.Shared.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;
using RecordAuditTrails.Services.Auditing;
using RecordAuditTrails.Services.Documents;
using RecordAuditTrails.Services.Records;
using RecordAuditTrails.Services.Retention;

namespace RecordAuditTrails.Services.Tests;

public class SystemDocumentServiceTests
{
    private static CreateDocumentRequest NewDoc(string number = "SOP-001", string role = "LabTechnician") =>
        new(number, "Audit trail review", "Operation", "Step 1: review.", role, null);

    [Fact]
    public async Task Create_StoresRevisionOneAsGenesisOfTheChain()
    {
        using var h = new Harness();

        var view = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);

        Assert.Equal(1, view.Summary.CurrentRevision);
        Assert.Equal("adam.admin", view.Summary.CreatedBy);
        Assert.Equal(CanonicalHasher.GenesisHash, view.Revision.PreviousHash);
        Assert.Equal(SystemDocumentService.DefaultCreateReason, view.Revision.ChangeDescription);
        Assert.Equal("adam.admin", view.Revision.Author);
        Assert.True((await h.Documents.VerifyRevisionsAsync(view.Summary.Id)).IsValid);
    }

    [Fact]
    public async Task Create_ValidatesInputAndRejectsDuplicateNumbers()
    {
        using var h = new Harness();
        await h.Documents.CreateAsync(NewDoc(), Harness.Admin);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Documents.CreateAsync(NewDoc(), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("") , Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc(role: "Janitor"), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("S2") with { Title = "" }, Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("S3") with { Category = "  " }, Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("S4") with { Content = " " }, Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("S5") with { Content = new string('x', 1_000_001) }, Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc("S6") with { Reason = "bad" }, Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.CreateAsync(NewDoc(new string('n', 101)), Harness.Admin));
        var custom = await h.Documents.CreateAsync(NewDoc("S7") with { Reason = "Approved by QA board" }, Harness.Admin);
        Assert.Equal("Approved by QA board", custom.Revision.ChangeDescription);
    }

    [Fact]
    public async Task Revise_AddsChainedRevisionAndKeepsEarlierOnesUntouched()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        h.Clock.Advance(TimeSpan.FromDays(30));

        var revised = await h.Documents.ReviseAsync(created.Summary.Id,
            new("Audit trail review v2", "Step 1: review. Step 2: sign.", "  Added signing step  ", 1), Harness.Admin);

        Assert.Equal(2, revised.Summary.CurrentRevision);
        Assert.Equal("Audit trail review v2", revised.Summary.Title);
        Assert.Equal(created.Revision.Hash, revised.Revision.PreviousHash);
        Assert.Equal("Added signing step", revised.Revision.ChangeDescription);
        Assert.True(revised.Revision.CreatedUtc > created.Revision.CreatedUtc);

        var history = await h.Documents.GetRevisionHistoryAsync(created.Summary.Id, Harness.Tech);
        Assert.Equal(["Step 1: review.", "Step 1: review. Step 2: sign."], history.Select(r => r.Content));
        Assert.True((await h.Documents.VerifyRevisionsAsync(created.Summary.Id)).IsValid);
    }

    [Fact]
    public async Task Revise_KeepsTitleWhenNoneGiven()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        var revised = await h.Documents.ReviseAsync(created.Summary.Id, new(null, "new body", "Body updated", null), Harness.Admin);
        Assert.Equal("Audit trail review", revised.Revision.Title);
    }

    [Fact]
    public async Task Revise_ValidatesReasonContentChangeAndVersion()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        var id = created.Summary.Id;

        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.ReviseAsync(Guid.NewGuid(), new(null, "x", "valid reason", null), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.ReviseAsync(id, new(null, "x", "", null), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.ReviseAsync(id, new(null, "", "valid reason", null), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.ReviseAsync(id, new("  ", "different", "valid reason", null), Harness.Admin));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.ReviseAsync(id, new(null, "Step 1: review.", "No real change", null), Harness.Admin));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Documents.ReviseAsync(id, new(null, "other", "valid reason", 7), Harness.Admin));
    }

    [Fact]
    public async Task Revise_FailsClearlyWhenARevisionRowIsMissing()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        await h.TamperAsync("DROP TABLE document_revisions");
        await Assert.ThrowsAnyAsync<Exception>(() => h.Documents.ReviseAsync(created.Summary.Id, new(null, "x", "valid reason", null), Harness.Admin));
    }

    [Fact]
    public async Task List_ShowsOnlyDocumentsTheCallerMayRead()
    {
        using var h = new Harness();
        await h.Documents.CreateAsync(NewDoc("SOP-PUBLIC", "LabTechnician"), Harness.Admin);
        await h.Documents.CreateAsync(NewDoc("SOP-MGR", "LabManager"), Harness.Admin);
        await h.Documents.CreateAsync(NewDoc("SOP-ADMIN", "Administrator"), Harness.Admin);

        Assert.Equal(["SOP-PUBLIC"], (await h.Documents.ListAsync(Harness.Tech)).Select(d => d.DocumentNumber));
        Assert.Equal(["SOP-MGR", "SOP-PUBLIC"], (await h.Documents.ListAsync(Harness.Manager)).Select(d => d.DocumentNumber));
        Assert.Equal(3, (await h.Documents.ListAsync(Harness.Admin)).Count);
    }

    [Fact]
    public async Task Get_EnforcesMinimumRoleAndLogsEveryView()
    {
        using var h = new Harness();
        var restricted = await h.Documents.CreateAsync(NewDoc("SOP-MGR", "LabManager"), Harness.Admin);
        var id = restricted.Summary.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Documents.GetAsync(id, null, Harness.Tech));
        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Documents.GetRevisionHistoryAsync(id, Harness.Reviewer));
        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Documents.DistributeAsync(id, new("QA", null), Harness.Reviewer));
        Assert.Empty(await h.Documents.GetAccessLogAsync(id, 0, 10)); // denied attempts do not read the document

        var view = await h.Documents.GetAsync(id, null, Harness.Manager);
        Assert.Equal(1, view.Revision.RevisionNumber);
        await h.Documents.GetAsync(id, 1, Harness.Admin);

        var log = await h.Documents.GetAccessLogAsync(id, 0, 10);
        Assert.Equal(["mia.manager", "adam.admin"], log.Select(l => l.Username));
        Assert.All(log, l => Assert.Equal(AccessAction.View, l.Action));
        Assert.All(log, l => Assert.Equal(AccessSubjects.Document, l.SubjectType));
    }

    [Fact]
    public async Task Get_ReturnsRequestedHistoricalRevision()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        await h.Documents.ReviseAsync(created.Summary.Id, new(null, "v2 body", "second edition", null), Harness.Admin);

        Assert.Equal("Step 1: review.", (await h.Documents.GetAsync(created.Summary.Id, 1, Harness.Tech)).Revision.Content);
        Assert.Equal("v2 body", (await h.Documents.GetAsync(created.Summary.Id, null, Harness.Tech)).Revision.Content);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.GetAsync(created.Summary.Id, 9, Harness.Tech));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.GetAsync(Guid.NewGuid(), null, Harness.Tech));
    }

    [Fact]
    public async Task Distribute_LogsRecipientRevisionAndNote()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        var id = created.Summary.Id;

        var plain = await h.Documents.DistributeAsync(id, new("  QA department ", null), Harness.Manager);
        var noted = await h.Documents.DistributeAsync(id, new("Auditor", " for FDA inspection "), Harness.Manager);

        Assert.Equal(AccessAction.Distribute, plain.Action);
        Assert.Equal("revision 1 to QA department", plain.Details);
        Assert.Equal("revision 1 to Auditor: for FDA inspection", noted.Details);
        Assert.Equal(2, (await h.Documents.GetAccessLogAsync(id, 0, 10)).Count);
        Assert.Single(await h.Documents.GetAccessLogAsync(id, 1, 10));
    }

    [Fact]
    public async Task Distribute_ValidatesInput()
    {
        using var h = new Harness();
        var id = (await h.Documents.CreateAsync(NewDoc(), Harness.Admin)).Summary.Id;
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.DistributeAsync(id, new(" ", null), Harness.Manager));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Documents.DistributeAsync(id, new("QA", new string('n', 1000)), Harness.Manager));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.DistributeAsync(Guid.NewGuid(), new("QA", null), Harness.Manager));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.GetAccessLogAsync(Guid.NewGuid(), 0, 10));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Documents.VerifyRevisionsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Verify_DetectsRevisionContentChangedDirectlyInTheDatabase()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        await h.Documents.ReviseAsync(created.Summary.Id, new(null, "second", "second edition", null), Harness.Admin);

        await h.TamperAsync("UPDATE document_revisions SET Content = 'quietly rewritten' WHERE RevisionNumber = 1");

        var result = await h.Documents.VerifyRevisionsAsync(created.Summary.Id);
        Assert.False(result.IsValid);
        Assert.Equal(1, result.FirstBrokenPosition);
    }

    [Fact]
    public async Task UnknownStoredRoleIsTreatedAsReadableByAnyAuthenticatedUser()
    {
        using var h = new Harness();
        var created = await h.Documents.CreateAsync(NewDoc(), Harness.Admin);
        await h.TamperAsync("UPDATE system_documents SET MinimumReadRole = 'Legacy'");
        Assert.Single(await h.Documents.ListAsync(Harness.Tech));
    }
}

public class RetentionServiceTests
{
    [Fact]
    public async Task Status_ReportsRetainUntilAndAuditTrailRetentionAtLeastAsLong()
    {
        using var h = new Harness(o => o.DefaultYears = 7);
        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        var status = await h.Retention.GetStatusAsync(record.Id);

        Assert.Equal(record.RetainUntilUtc, status.RetainUntilUtc);
        Assert.True(status.AuditTrailRetainUntilUtc >= status.RetainUntilUtc);
        Assert.False(status.RetentionExpired);
        Assert.False(status.IsDeleted);
        Assert.Equal("BatchRecord", status.RecordType);
    }

    [Fact]
    public async Task ExpiryIsEvaluatedAgainstTheClock_AndDeletedRecordsAreRetainedToo()
    {
        using var h = new Harness(o => o.DefaultYears = 2);
        var keep = await h.Records.CreateAsync(Harness.NewRecord("Long", "keep"), Harness.Tech);
        var old = await h.Records.CreateAsync(Harness.NewRecord("Short", "old"), Harness.Tech);
        await h.Records.DeleteAsync(old.Id, new("entered in error", null), Harness.Tech);
        Assert.Empty(await h.Retention.ListExpiredAsync(0, 10));

        h.Clock.Advance(TimeSpan.FromDays(366 * 2 + 1));

        var expired = await h.Retention.ListExpiredAsync(0, 0);
        Assert.Equal(2, expired.Count); // nothing is purged automatically
        Assert.All(expired, e => Assert.True(e.RetentionExpired));
        Assert.Contains(expired, e => e.IsDeleted);
        Assert.True((await h.Retention.GetStatusAsync(keep.Id)).RetentionExpired);
        Assert.Equal(2, (await h.Records.ListAsync(null, true, 0, 10)).Count);
        Assert.Single(await h.Retention.ListExpiredAsync(1, 10));
    }

    [Fact]
    public async Task Status_UnknownRecordIsNotFound()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Retention.GetStatusAsync(Guid.NewGuid()));
    }
}

public class ServiceRegistrationTests
{
    [Fact]
    public void AddAuditServices_RegistersEverythingAndBindsRetentionConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Retention:DefaultYears"] = "3",
            ["Retention:ByRecordType:Special"] = "20",
        }).Build();
        var services = new ServiceCollection();
        services.AddDbContext<Database.AuditDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddAuditRepositories();
        services.AddAuditServices(config);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        foreach (var type in new[]
        {
            typeof(IRecordService), typeof(IAuditTrailService), typeof(IRecordExportService), typeof(IRetentionService),
            typeof(ISystemDocumentService), typeof(IAuditTrailWriter),
        })
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(type));
        }
        var policy = provider.GetRequiredService<Core.Retention.RetentionPolicy>();
        Assert.Equal(3, policy.YearsFor("anything"));
        Assert.Equal(20, policy.YearsFor("Special"));
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Actor_CarriesIdentityAndRole()
    {
        var actor = new Actor("id", "name", UserRole.Reviewer);
        Assert.Equal(("id", "name", UserRole.Reviewer), (actor.UserId, actor.Username, actor.Role));
    }
}

public class AuditTrailWriterTests
{
    [Fact]
    public async Task Append_WithNoChangesWritesNothing()
    {
        using var h = new Harness();
        var writer = new AuditTrailWriter(new Database.AuditTrailRepository(h.Db));
        var entries = await writer.AppendAsync(Guid.NewGuid(), 1, AuditAction.Modify, [], "nothing changed", Harness.Tech, DateTime.UtcNow);
        Assert.Empty(entries);
    }
}

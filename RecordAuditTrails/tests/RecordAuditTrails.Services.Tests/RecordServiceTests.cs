using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Models;
using RecordAuditTrails.Services.Records;

namespace RecordAuditTrails.Services.Tests;

public class RecordServiceCreateTests
{
    [Fact]
    public async Task Create_StoresRecordAndOneAuditEntryPerField()
    {
        using var h = new Harness();

        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        Assert.Equal(1, record.Version);
        Assert.False(record.IsDeleted);
        Assert.Equal("tina.tech", record.CreatedBy);
        var entries = await h.Trail.GetForRecordAsync(record.Id, new());
        Assert.Equal(["RecordType", "Title", "operator", "pH"], entries.Select(e => e.FieldName).Order(StringComparer.Ordinal));
        Assert.All(entries, e =>
        {
            Assert.Equal(AuditAction.Create, e.Action);
            Assert.Null(e.OldValue);
            Assert.NotNull(e.NewValue);
            Assert.Equal("tina.tech", e.Username);
            Assert.Equal("u-tech", e.UserId);
            Assert.Equal("LabTechnician", e.Role);
            Assert.Equal(RecordService.DefaultCreateReason, e.Reason);
            Assert.Equal(record.CreatedUtc, e.TimestampUtc);
        });
        Assert.True((await h.Trail.VerifyRecordAsync(record.Id)).IsValid);
    }

    [Fact]
    public async Task Create_TimestampIsServerGeneratedAndTruncatedToMicroseconds()
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        Assert.Equal(TimestampPrecision.ToStorage(h.Clock.GetUtcNow()), record.CreatedUtc);
        Assert.Equal(0, record.CreatedUtc.Ticks % 10);
    }

    [Fact]
    public async Task Create_UsesSuppliedReasonWhenPresent()
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(Harness.NewRecord() with { Reason = "Entered from paper batch sheet" }, Harness.Tech);
        var entries = await h.Trail.GetForRecordAsync(record.Id, new());
        Assert.All(entries, e => Assert.Equal("Entered from paper batch sheet", e.Reason));
    }

    [Fact]
    public async Task Create_SetsRetentionFromRecordTypeRuleOrDefault()
    {
        using var h = new Harness(o => { o.DefaultYears = 3; o.ByRecordType["ClinicalTrial"] = 15; });
        var clinical = await h.Records.CreateAsync(Harness.NewRecord("ClinicalTrial"), Harness.Tech);
        var other = await h.Records.CreateAsync(Harness.NewRecord("Other"), Harness.Tech);
        Assert.Equal(clinical.CreatedUtc.AddYears(15), clinical.RetainUntilUtc);
        Assert.Equal(other.CreatedUtc.AddYears(3), other.RetainUntilUtc);
    }

    [Fact]
    public async Task Create_WithNoFields_RecordsTypeAndTitleOnly()
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(new("T", "title", null, null), Harness.Tech);
        Assert.Equal(2, (await h.Trail.GetForRecordAsync(record.Id, new())).Count);
    }

    [Theory]
    [InlineData("", "title")]
    [InlineData("  ", "title")]
    [InlineData("type", "")]
    [InlineData("type", null)]
    public async Task Create_RejectsMissingTypeOrTitle(string type, string? title)
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.CreateAsync(new(type, title!, null, null), Harness.Tech));
    }

    [Fact]
    public async Task Create_RejectsOverlongTypeTitleAndInvalidFieldsOrReason()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.CreateAsync(new(new string('t', 101), "x", null, null), Harness.Tech));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.CreateAsync(new("t", new string('x', 501), null, null), Harness.Tech));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.CreateAsync(new("t", "x", new Dictionary<string, string?> { ["Title"] = "spoof" }, null), Harness.Tech));
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.CreateAsync(new("t", "x", null, "bad"), Harness.Tech));
        Assert.Empty(await h.Records.ListAsync(null, true, 0, 10));
    }
}

public class RecordServiceUpdateTests
{
    [Fact]
    public async Task Update_RetainsOldValueAndRecordsNewValueWhoWhenAndWhy()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        h.Clock.Advance(TimeSpan.FromHours(2));

        var updated = await h.Records.UpdateAsync(created.Id,
            new(null, new Dictionary<string, string?> { ["pH"] = "7.4", ["operator"] = "tina" }, "  Corrected transcription error  ", null),
            Harness.Reviewer);

        Assert.Equal(2, updated.Version);
        Assert.Equal("7.4", updated.Fields["pH"]);
        Assert.Equal("rob.reviewer", updated.ModifiedBy);
        var all = await h.Trail.GetForRecordAsync(created.Id, new());
        var change = Assert.Single(all, e => e.Action == AuditAction.Modify);
        Assert.Equal("pH", change.FieldName);
        Assert.Equal("7.0", change.OldValue);
        Assert.Equal("7.4", change.NewValue);
        Assert.Equal("Corrected transcription error", change.Reason);
        Assert.Equal("rob.reviewer", change.Username);
        Assert.Equal("u-rev", change.UserId);
        Assert.Equal("Reviewer", change.Role);
        Assert.Equal(updated.ModifiedUtc, change.TimestampUtc);
        Assert.Equal(2, change.RecordVersion);
        // The original create entry for pH is still there, unchanged.
        Assert.Contains(all, e => e.Action == AuditAction.Create && e.FieldName == "pH" && e.NewValue == "7.0");
        Assert.True((await h.Trail.VerifyRecordAsync(created.Id)).IsValid);
    }

    [Fact]
    public async Task Update_ChangingSeveralFieldsSharesOneChangeSetAndExtendsTheChain()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        await h.Records.UpdateAsync(created.Id,
            new("New title", new Dictionary<string, string?> { ["pH"] = "8", ["extra"] = "x" }, "Multi-field correction", null), Harness.Tech);

        var entries = await h.Trail.GetForRecordAsync(created.Id, new(Action: AuditAction.Modify));
        // Title, extra (added), operator (removed), pH
        Assert.Equal(["Title", "extra", "operator", "pH"], entries.Select(e => e.FieldName));
        Assert.Single(entries.Select(e => e.ChangeSetId).Distinct());
        var removed = entries.Single(e => e.FieldName == "operator");
        Assert.Equal("tina", removed.OldValue);
        Assert.Null(removed.NewValue);
        Assert.Equal(entries.Select(e => e.Sequence).Order(), entries.Select(e => e.Sequence));
        Assert.True((await h.Trail.VerifyRecordAsync(created.Id)).IsValid);
    }

    [Fact]
    public async Task Update_TitleOnlyLeavesFieldsAlone()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        var updated = await h.Records.UpdateAsync(created.Id, new("Renamed", null, "Title was wrong", null), Harness.Tech);
        Assert.Equal("Renamed", updated.Title);
        Assert.Equal(created.Fields, updated.Fields);
    }

    [Fact]
    public async Task Update_RequiresReasonAndAnActualChange()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.UpdateAsync(created.Id, new("x", null, "", null), Harness.Tech));
        var noop = await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.UpdateAsync(created.Id, new("Batch 1", null, "No actual change", null), Harness.Tech));
        Assert.Contains("does not change", noop.Message);
        Assert.Equal(1, (await h.Records.GetAsync(created.Id)).Version);
    }

    [Fact]
    public async Task Update_RejectsInvalidTitleAndFields()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.UpdateAsync(created.Id, new("  ", null, "valid reason", null), Harness.Tech));
        await Assert.ThrowsAsync<AuditValidationException>(() =>
            h.Records.UpdateAsync(created.Id, new(null, new Dictionary<string, string?> { ["IsDeleted"] = "true" }, "valid reason", null), Harness.Tech));
    }

    [Fact]
    public async Task Update_UnknownRecordIsNotFound()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Records.UpdateAsync(Guid.NewGuid(), new("x", null, "valid reason", null), Harness.Tech));
    }

    [Fact]
    public async Task Update_WithStaleExpectedVersionIsConflict()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        await h.Records.UpdateAsync(created.Id, new("v2", null, "first change", 1), Harness.Tech);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Records.UpdateAsync(created.Id, new("v3", null, "stale change", 1), Harness.Tech));
        Assert.Equal("v2", (await h.Records.GetAsync(created.Id)).Title);
    }

    [Fact]
    public async Task Update_DeletedRecordCannotBeChanged()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        await h.Records.DeleteAsync(created.Id, new("Entered in error", null), Harness.Tech);
        var ex = await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.UpdateAsync(created.Id, new("x", null, "valid reason", null), Harness.Tech));
        Assert.Contains("deleted", ex.Message);
    }

    [Fact]
    public async Task Update_ByTwoWritersWithStaleStateIsDetectedByTheDatabase()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        // A second unit of work loads the record, then another writer commits, then the second writer saves.
        var second = new Harness2(h);
        var loaded = await second.Db.Records.FindAsync(created.Id);
        await h.Records.UpdateAsync(created.Id, new("winner", null, "first writer wins", null), Harness.Tech);

        loaded!.Title = "loser";
        loaded.Version++;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new Database.UnitOfWork(second.Db).SaveChangesAsync());
    }

    private sealed class Harness2(Harness parent) : IDisposable
    {
        public Database.AuditDbContext Db { get; } = parent.NewContext();

        public void Dispose() => Db.Dispose();
    }
}

public class RecordServiceDeleteAndReadTests
{
    [Fact]
    public async Task Delete_IsLogicalAndKeepsFullAuditTrail()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        var deleted = await h.Records.DeleteAsync(created.Id, new("  Duplicate entry  ", 1), Harness.Manager);

        Assert.True(deleted.IsDeleted);
        Assert.Equal(2, deleted.Version);
        Assert.True((await h.Records.GetAsync(created.Id)).IsDeleted);
        var entry = Assert.Single(await h.Trail.GetForRecordAsync(created.Id, new(Action: AuditAction.Delete)));
        Assert.Equal("IsDeleted", entry.FieldName);
        Assert.Equal("false", entry.OldValue);
        Assert.Equal("true", entry.NewValue);
        Assert.Equal("Duplicate entry", entry.Reason);
        Assert.Equal("mia.manager", entry.Username);
        Assert.Equal(4 + 1, (await h.Trail.GetForRecordAsync(created.Id, new())).Count);
        Assert.True((await h.Trail.VerifyRecordAsync(created.Id)).IsValid);
    }

    [Fact]
    public async Task Delete_ValidatesReasonStateAndVersion()
    {
        using var h = new Harness();
        var created = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.DeleteAsync(created.Id, new("", null), Harness.Tech));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Records.DeleteAsync(Guid.NewGuid(), new("valid reason", null), Harness.Tech));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => h.Records.DeleteAsync(created.Id, new("valid reason", 9), Harness.Tech));
        await h.Records.DeleteAsync(created.Id, new("valid reason", null), Harness.Tech);
        await Assert.ThrowsAsync<AuditValidationException>(() => h.Records.DeleteAsync(created.Id, new("valid reason", null), Harness.Tech));
    }

    [Fact]
    public async Task Get_UnknownRecordIsNotFound()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Records.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task List_FiltersAndPagesAndHidesDeletedByDefault()
    {
        using var h = new Harness();
        var a = await h.Records.CreateAsync(Harness.NewRecord("A", "a"), Harness.Tech);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Records.CreateAsync(Harness.NewRecord("B", "b"), Harness.Tech);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var c = await h.Records.CreateAsync(Harness.NewRecord("A", "c"), Harness.Tech);
        await h.Records.DeleteAsync(c.Id, new("deleted for test", null), Harness.Tech);

        Assert.Equal(["a", "b"], (await h.Records.ListAsync(null, false, 0, 0)).Select(r => r.Title));
        Assert.Equal(["a", "c"], (await h.Records.ListAsync("A", true, 0, 10)).Select(r => r.Title));
        Assert.Equal(["b"], (await h.Records.ListAsync(null, false, 1, 1)).Select(r => r.Title));
        Assert.Equal(3, (await h.Records.ListAsync(null, true, -5, 100_000)).Count);
        Assert.Equal(a.Id, (await h.Records.ListAsync(null, true, 0, 1)).Single().Id);
    }
}

public class PagingTests
{
    [Theory]
    [InlineData(-1, 0, 0, 100)]
    [InlineData(5, 10, 5, 10)]
    [InlineData(0, 100_000, 0, 500)]
    [InlineData(0, -3, 0, 100)]
    public void Normalize_ClampsToSafeBounds(int skip, int take, int expectedSkip, int expectedTake) =>
        Assert.Equal((expectedSkip, expectedTake), Paging.Normalize(skip, take));
}

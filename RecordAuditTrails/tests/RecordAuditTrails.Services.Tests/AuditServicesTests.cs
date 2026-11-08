using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Models;
using RecordAuditTrails.Services.Auditing;

namespace RecordAuditTrails.Services.Tests;

public class AuditTrailServiceTests
{
    private static async Task<(Harness H, Guid Id)> SeedAsync()
    {
        var h = new Harness();
        var r = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        h.Clock.Advance(TimeSpan.FromDays(1));
        await h.Records.UpdateAsync(r.Id, new(null, new Dictionary<string, string?> { ["pH"] = "9", ["operator"] = "tina" }, "Recalibrated probe", null), Harness.Reviewer);
        return (h, r.Id);
    }

    [Fact]
    public async Task GetForRecord_FiltersByUserActionFieldAndDateRange()
    {
        var (h, id) = await SeedAsync();
        using var _ = h;

        Assert.Equal(5, (await h.Trail.GetForRecordAsync(id, new())).Count);
        Assert.Single(await h.Trail.GetForRecordAsync(id, new(Username: "rob.reviewer")));
        Assert.Single(await h.Trail.GetForRecordAsync(id, new(Action: AuditAction.Modify)));
        Assert.Equal(2, (await h.Trail.GetForRecordAsync(id, new(FieldName: "pH"))).Count);
        var cutoff = h.Clock.GetUtcNow().UtcDateTime.AddHours(-1);
        Assert.Single(await h.Trail.GetForRecordAsync(id, new(FromUtc: cutoff)));
        Assert.Equal(4, (await h.Trail.GetForRecordAsync(id, new(ToUtc: cutoff))).Count);
        Assert.Equal(2, (await h.Trail.GetForRecordAsync(id, new(Skip: 3, Take: 10))).Count);
    }

    [Fact]
    public async Task GetForRecord_UnknownRecordIsNotFound()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Trail.GetForRecordAsync(Guid.NewGuid(), new()));
        await Assert.ThrowsAsync<NotFoundException>(() => h.Trail.VerifyRecordAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Search_SpansRecords()
    {
        var (h, _) = await SeedAsync();
        using var _h = h;
        await h.Records.CreateAsync(Harness.NewRecord("Other", "o"), Harness.Manager);

        Assert.Equal(5 + 4, (await h.Trail.SearchAsync(new())).Count);
    }

    [Fact]
    public async Task Verify_DetectsTamperingMadeDirectlyInTheDatabase()
    {
        var (h, id) = await SeedAsync();
        using var _ = h;
        Assert.True((await h.Trail.VerifyRecordAsync(id)).IsValid);

        await h.TamperAsync("UPDATE audit_trail SET NewValue = '1.0' WHERE FieldName = 'pH' AND Action = 'Modify'");

        var result = await h.Trail.VerifyRecordAsync(id);
        Assert.False(result.IsValid);
        Assert.Equal(5, result.FirstBrokenPosition);
    }

    [Fact]
    public async Task Verify_DetectsDeletedEntryDirectlyInTheDatabase()
    {
        var (h, id) = await SeedAsync();
        using var _ = h;
        await h.TamperAsync("DELETE FROM audit_trail WHERE Sequence = 2");
        Assert.False((await h.Trail.VerifyRecordAsync(id)).IsValid);
    }

    [Fact]
    public async Task VerifyAll_ReportsOnlyTamperedRecords()
    {
        var (h, id) = await SeedAsync();
        using var _ = h;
        var other = await h.Records.CreateAsync(Harness.NewRecord("Other", "o"), Harness.Tech);

        var clean = await h.Trail.VerifyAllAsync();
        Assert.True(clean.IsValid);
        Assert.Equal(2, clean.RecordsChecked);

        await h.TamperAsync($"UPDATE audit_trail SET Reason = 'rewritten' WHERE RecordId = '{id.ToString().ToUpperInvariant()}' AND Sequence = 1");
        var dirty = await h.Trail.VerifyAllAsync();
        Assert.False(dirty.IsValid);
        var failure = Assert.Single(dirty.Failures);
        Assert.Equal(id, failure.RecordId);
        Assert.DoesNotContain(dirty.Failures, f => f.RecordId == other.Id);
    }

    [Fact]
    public async Task AuditTrailRemainsReadableEvenIfTheRecordRowIsMissing()
    {
        var (h, id) = await SeedAsync();
        using var _ = h;
        h.Db.ChangeTracker.Clear();
        await h.TamperAsync($"DELETE FROM records");

        Assert.Equal(5, (await h.Trail.GetForRecordAsync(id, new())).Count);
        Assert.True((await h.Trail.VerifyRecordAsync(id)).IsValid);
    }
}

public class RecordExportServiceTests
{
    [Theory]
    [InlineData(ExportFormat.Json, "application/json")]
    [InlineData(ExportFormat.Csv, "text/csv")]
    [InlineData(ExportFormat.Text, "text/plain")]
    [InlineData(ExportFormat.Html, "text/html")]
    public async Task Export_ProducesCompleteCopyInEveryFormatAndLogsTheExport(ExportFormat format, string contentType)
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        await h.Records.UpdateAsync(record.Id, new("Batch 1 (rev)", null, "Title clarified", null), Harness.Tech);

        var exported = await h.Export.ExportAsync(record.Id, format, Harness.Reviewer);

        Assert.Equal(contentType, exported.ContentType);
        Assert.Contains("Batch 1 (rev)", exported.Content);
        Assert.Contains("Title clarified", exported.Content);
        Assert.Contains("Batch 1", exported.Content); // the old value is part of the copy too
        Assert.Equal(Core.Hashing.CanonicalHasher.Sha256Hex(exported.Content), exported.Sha256);

        var logged = await h.Db.AccessLog.SingleAsync();
        Assert.Equal(AccessAction.Export, logged.Action);
        Assert.Equal(record.Id, logged.SubjectId);
        Assert.Equal("rob.reviewer", logged.Username);
        Assert.Contains(exported.Sha256, logged.Details);
    }

    [Fact]
    public async Task Export_JsonIncludesIntegrityVerdictAndAllEntries()
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);

        var exported = await h.Export.ExportAsync(record.Id, ExportFormat.Json, Harness.Reviewer);

        using var doc = JsonDocument.Parse(exported.Content);
        Assert.True(doc.RootElement.GetProperty("integrity").GetProperty("isValid").GetBoolean());
        Assert.Equal(4, doc.RootElement.GetProperty("auditTrail").GetArrayLength());
        Assert.Equal("rob.reviewer", doc.RootElement.GetProperty("generatedBy").GetString());
    }

    [Fact]
    public async Task Export_FlagsTamperedTrailInsteadOfHidingIt()
    {
        using var h = new Harness();
        var record = await h.Records.CreateAsync(Harness.NewRecord(), Harness.Tech);
        await h.TamperAsync("UPDATE audit_trail SET NewValue = 'forged' WHERE FieldName = 'pH'");

        var exported = await h.Export.ExportAsync(record.Id, ExportFormat.Text, Harness.Reviewer);

        Assert.Contains("AUDIT TRAIL INTEGRITY FAILURE", exported.Content);
    }

    [Fact]
    public async Task Export_UnknownRecordIsNotFoundAndNothingIsLogged()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<NotFoundException>(() => h.Export.ExportAsync(Guid.NewGuid(), ExportFormat.Json, Harness.Reviewer));
        Assert.Empty(h.Db.AccessLog);
    }
}

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

public class AlertTests
{
    private readonly Harness _h = new();

    [Fact]
    public async Task Raise_StoresFirst_ThenNotifiesEverySink()
    {
        var alert = await _h.Alerts.RaiseAsync(AlertType.IdentityMismatch, AlertSeverity.Critical, "u1", "tina", "detail", _h.Tech);
        Assert.Same(alert, Assert.Single(_h.Sink.Alerts));
        Assert.Equal(alert.Id, Assert.Single(_h.Data.Alerts).Id);
        Assert.Equal(Harness.Start.UtcDateTime, alert.RaisedUtc);
    }

    [Fact]
    public async Task FailingSink_NeverBlocksTheOthersOrTheCaller()
    {
        var sink = new CollectingAlertSink();
        var service = new SecurityAlertService(_h.SecurityStore, [new ThrowingAlertSink(), sink], _h.Clock, NullLogger<SecurityAlertService>.Instance);
        var alert = await service.RaiseAsync(AlertType.SignerLockedOut, AlertSeverity.Critical, "u", "n", "d", null);
        Assert.Same(alert, Assert.Single(sink.Alerts));
        Assert.Single(_h.Data.Alerts);
    }

    [Fact]
    public async Task CancellationInASinkPropagates()
    {
        var service = new SecurityAlertService(_h.SecurityStore, [new CancellingSink()], _h.Clock, NullLogger<SecurityAlertService>.Instance);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.RaiseAsync(AlertType.SignerLockedOut, AlertSeverity.Critical, "u", "n", "d", null));
    }

    private sealed class CancellingSink : ISecurityAlertSink
    {
        public Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task List_FiltersBySeverityAndDate_NewestFirst_AndPages()
    {
        await _h.Alerts.RaiseAsync(AlertType.FailedSignatureAuthentication, AlertSeverity.Warning, "u", "n", "first", null);
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.Alerts.RaiseAsync(AlertType.SignerLockedOut, AlertSeverity.Critical, "u", "n", "second", null);
        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        await _h.Alerts.RaiseAsync(AlertType.SignerDeauthorized, AlertSeverity.Info, "u", "n", "third", null);

        Assert.Equal(["third", "second", "first"], (await _h.Alerts.ListAsync(null, null, 0, 0)).Select(a => a.Detail));
        Assert.Equal(["second"], (await _h.Alerts.ListAsync(AlertSeverity.Critical, null, 0, 0)).Select(a => a.Detail));
        Assert.Equal(["third", "second"], (await _h.Alerts.ListAsync(null, Harness.Start.UtcDateTime.AddSeconds(30), 0, 0)).Select(a => a.Detail));
        Assert.Equal(["second"], (await _h.Alerts.ListAsync(null, null, 1, 1)).Select(a => a.Detail));
        Assert.Equal(3, (await _h.Alerts.ListAsync(null, null, -5, 10_000)).Count);
    }

    [Fact]
    public void Paging_NormalizesBounds()
    {
        Assert.Equal((0, 100), Paging.Normalize(-1, 0));
        Assert.Equal((5, 500), Paging.Normalize(5, 9999));
        Assert.Equal((2, 7), Paging.Normalize(2, 7));
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, LogLevel.Critical)]
    [InlineData(AlertSeverity.Warning, LogLevel.Warning)]
    [InlineData(AlertSeverity.Info, LogLevel.Information)]
    public async Task LoggingSink_UsesALevelMatchingTheSeverity(AlertSeverity severity, LogLevel expected)
    {
        var logger = new ListLogger<LoggingAlertSink>();
        await new LoggingAlertSink(logger).PublishAsync(
            new SecurityAlert(Guid.NewGuid(), DateTime.UtcNow, severity, AlertType.SignerLockedOut, "u", "tina", "boom", null), null);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(expected, entry.Level);
        Assert.Contains("SECURITY-ALERT", entry.Message);
        Assert.Contains("boom", entry.Message);
    }

    [Fact]
    public async Task AuditTrailSink_RecordsTheAlertUsingTheCallersToken()
    {
        var alert = new SecurityAlert(Guid.NewGuid(), Harness.Start.UtcDateTime, AlertSeverity.Critical, AlertType.IdentityMismatch, "u", "tina", "detail", null);
        await new AuditTrailAlertSink(_h.Audit).PublishAsync(alert, _h.Tech);
        var record = Assert.Single(_h.Audit.OfType(AuditFields.AlertRecordType));
        Assert.Equal("tok-tina", record.Tokens.Single());
        Assert.Equal("Critical", record.Fields["severity"]);
        Assert.Equal("IdentityMismatch", record.Fields["type"]);
        Assert.Contains("IdentityMismatch", record.Reasons.Single());
    }

    [Fact]
    public async Task AuditTrailSink_WithoutACaller_DoesNothing()
    {
        var alert = new SecurityAlert(Guid.NewGuid(), Harness.Start.UtcDateTime, AlertSeverity.Info, AlertType.SignerDeauthorized, null, null, "d", null);
        await new AuditTrailAlertSink(_h.Audit).PublishAsync(alert, null);
        Assert.Empty(_h.Audit.Records);
    }

    [Fact]
    public async Task WebhookSink_PostsTheAlertAsJson()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sink = new WebhookAlertSink(new HttpClient(handler), TestOptions.Wrap(o => o.AlertWebhookUrl = "http://soc.test/hook"));
        await sink.PublishAsync(new SecurityAlert(Guid.NewGuid(), Harness.Start.UtcDateTime, AlertSeverity.Critical, AlertType.SignerLockedOut, "u1", "tina", "locked", null), null);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://soc.test/hook", request.Uri);
        Assert.Contains("\"severity\":\"Critical\"", request.Body);
        Assert.Contains("\"type\":\"SignerLockedOut\"", request.Body);
        Assert.Contains("locked", request.Body);
    }

    [Fact]
    public async Task WebhookSink_WithoutAUrl_DoesNothing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var sink = new WebhookAlertSink(new HttpClient(handler), TestOptions.Wrap(o => o.AlertWebhookUrl = " "));
        await sink.PublishAsync(new SecurityAlert(Guid.NewGuid(), DateTime.UtcNow, AlertSeverity.Info, AlertType.SignerLockedOut, null, null, "d", null), null);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WebhookSink_ReportsAFailedDelivery()
    {
        var sink = new WebhookAlertSink(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))),
            TestOptions.Wrap(o => o.AlertWebhookUrl = "http://soc.test/hook"));
        await Assert.ThrowsAsync<HttpRequestException>(() => sink.PublishAsync(
            new SecurityAlert(Guid.NewGuid(), DateTime.UtcNow, AlertSeverity.Info, AlertType.SignerLockedOut, null, null, "d", null), null));
    }
}

public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Seen(string Method, string Uri, string Body, string? Authorization);

    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Seen(request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
        return respond(request);
    }
}

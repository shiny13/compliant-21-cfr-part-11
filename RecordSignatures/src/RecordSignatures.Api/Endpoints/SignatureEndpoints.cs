using AuthenticationCore.Shared.Authorization;
using RecordSignatures.Api.Security;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Reporting;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Api.Endpoints;

public static class SignatureEndpoints
{
    public static IEndpointRouteBuilder MapSignatureEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/meanings", () => Results.Ok(MeaningCatalog.All.Select(m => new
            {
                m.Code,
                m.DisplayName,
                m.Statement,
                minimumRole = m.MinimumRole.ToString(),
            })))
            .WithTags("Signatures").RequireAuthorization(RolePolicies.LabTechnician);

        var signatures = api.MapGroup("/signatures").WithTags("Signatures");

        signatures.MapPost("", async (SignRequest request, HttpContext http, ISignatureService service, CancellationToken ct) =>
        {
            var signature = await service.SignAsync(request, OperatorFactory.FromHttpContext(http), ct);
            return Results.Created($"/api/signatures/{signature.Id}", signature);
        }).RequireAuthorization(RolePolicies.LabTechnician);

        signatures.MapGet("/{id:guid}", async (Guid id, HttpContext http, ISignatureService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        signatures.MapPost("/{id:guid}/revoke", async (Guid id, RevokeSignatureRequest request, HttpContext http, ISignatureService service, CancellationToken ct) =>
            Results.Ok(await service.RevokeAsync(id, request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        var records = api.MapGroup("/records/{recordId:guid}/signatures").WithTags("Signed records");

        records.MapGet("", async (Guid recordId, HttpContext http, ISignatureService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyRecordAsync(recordId, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        records.MapGet("/report", async (Guid recordId, string? format, HttpContext http, ISignatureReportService service, CancellationToken ct) =>
        {
            if (!SignatureReportExporter.TryParseFormat(format ?? nameof(ReportFormat.Json), out var parsed))
            {
                throw new SignatureValidationException($"Format must be one of: {string.Join(", ", Enum.GetNames<ReportFormat>())}.");
            }
            var report = await service.ExportAsync(recordId, parsed, OperatorFactory.FromHttpContext(http), ct);
            http.Response.Headers["X-Content-SHA256"] = report.Sha256;
            return Results.Text(report.Content, $"{report.ContentType}; charset=utf-8");
        }).RequireAuthorization(RolePolicies.Reviewer);

        api.MapGet("/security-alerts", async (string? minimumSeverity, DateTimeOffset? from, int? skip, int? take, ISecurityAlertService service, CancellationToken ct) =>
        {
            AlertSeverity? severity = null;
            if (!string.IsNullOrWhiteSpace(minimumSeverity))
            {
                if (!Enum.TryParse<AlertSeverity>(minimumSeverity, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                {
                    throw new SignatureValidationException($"Severity must be one of: {string.Join(", ", Enum.GetNames<AlertSeverity>())}.");
                }
                severity = value;
            }
            return Results.Ok(await service.ListAsync(severity, from?.UtcDateTime, skip ?? 0, take ?? 0, ct));
        }).WithTags("Security").RequireAuthorization(RolePolicies.LabManager);

        return app;
    }
}

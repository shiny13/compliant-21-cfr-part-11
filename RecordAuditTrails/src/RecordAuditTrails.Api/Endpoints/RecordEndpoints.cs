using AuthenticationCore.Shared.Authorization;
using Microsoft.AspNetCore.Mvc;
using RecordAuditTrails.Api.Security;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Models;
using RecordAuditTrails.Services.Auditing;
using RecordAuditTrails.Services.Records;
using RecordAuditTrails.Services.Retention;

namespace RecordAuditTrails.Api.Endpoints;

public sealed record AuditQueryParameters(
    string? User,
    AuditAction? Action,
    string? Field,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Skip = 0,
    int Take = 100)
{
    public AuditTrailQuery ToQuery() => new(User, Action, Field, From?.UtcDateTime, To?.UtcDateTime, Skip, Take);
}

public static class RecordEndpoints
{
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder app)
    {
        var records = app.MapGroup("/api/records").WithTags("Records");

        records.MapPost("", async (CreateRecordRequest request, HttpContext http, IRecordService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ActorFactory.FromPrincipal(http.User), ct);
            return Results.Created($"/api/records/{created.Id}", created);
        }).RequireAuthorization(RolePolicies.LabTechnician);

        records.MapGet("", async (string? recordType, bool? includeDeleted, int? skip, int? take, IRecordService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(recordType, includeDeleted ?? false, skip ?? 0, take ?? 0, ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        records.MapGet("/{id:guid}", async (Guid id, IRecordService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        records.MapPut("/{id:guid}", async (Guid id, UpdateRecordRequest request, HttpContext http, IRecordService service, CancellationToken ct) =>
            Results.Ok(await service.UpdateAsync(id, request, ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        records.MapDelete("/{id:guid}", async (Guid id, [FromBody] DeleteRecordRequest request, HttpContext http, IRecordService service, CancellationToken ct) =>
            Results.Ok(await service.DeleteAsync(id, request, ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        records.MapGet("/{id:guid}/audit-trail", async (Guid id, [AsParameters] AuditQueryParameters query, IAuditTrailService service, CancellationToken ct) =>
            Results.Ok(await service.GetForRecordAsync(id, query.ToQuery(), ct)))
            .RequireAuthorization(RolePolicies.Reviewer);

        records.MapGet("/{id:guid}/audit-trail/verify", async (Guid id, IAuditTrailService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyRecordAsync(id, ct)))
            .RequireAuthorization(RolePolicies.Reviewer);

        records.MapGet("/{id:guid}/export", async (Guid id, string? format, HttpContext http, IRecordExportService service, CancellationToken ct) =>
        {
            if (!ReportExporter.TryParseFormat(format ?? nameof(ExportFormat.Json), out var parsed))
            {
                throw new AuditValidationException($"Format must be one of: {string.Join(", ", Enum.GetNames<ExportFormat>())}.");
            }
            var exported = await service.ExportAsync(id, parsed, ActorFactory.FromPrincipal(http.User), ct);
            http.Response.Headers["X-Content-SHA256"] = exported.Sha256;
            return Results.Text(exported.Content, $"{exported.ContentType}; charset=utf-8");
        }).RequireAuthorization(RolePolicies.Reviewer);

        records.MapGet("/{id:guid}/retention", async (Guid id, IRetentionService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatusAsync(id, ct)))
            .RequireAuthorization(RolePolicies.Reviewer);

        var admin = app.MapGroup("/api").WithTags("Audit trail");

        admin.MapGet("/audit-trail", async ([AsParameters] AuditQueryParameters query, IAuditTrailService service, CancellationToken ct) =>
            Results.Ok(await service.SearchAsync(query.ToQuery(), ct)))
            .RequireAuthorization(RolePolicies.Reviewer);

        admin.MapGet("/audit-trail/verify", async (IAuditTrailService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyAllAsync(ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        admin.MapGet("/retention/expired", async (int? skip, int? take, IRetentionService service, CancellationToken ct) =>
            Results.Ok(await service.ListExpiredAsync(skip ?? 0, take ?? 0, ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        return app;
    }
}

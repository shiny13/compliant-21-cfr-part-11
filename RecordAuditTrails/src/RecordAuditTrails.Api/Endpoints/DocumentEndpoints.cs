using AuthenticationCore.Shared.Authorization;
using RecordAuditTrails.Api.Security;
using RecordAuditTrails.Services.Documents;

namespace RecordAuditTrails.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var docs = app.MapGroup("/api/documents").WithTags("System documentation");

        docs.MapPost("", async (CreateDocumentRequest request, HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ActorFactory.FromPrincipal(http.User), ct);
            return Results.Created($"/api/documents/{created.Summary.Id}", created);
        }).RequireAuthorization(RolePolicies.Administrator);

        docs.MapGet("", async (HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        docs.MapGet("/{id:guid}", async (Guid id, int? revision, HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, revision, ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        docs.MapPost("/{id:guid}/revisions", async (Guid id, ReviseDocumentRequest request, HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
        {
            var revised = await service.ReviseAsync(id, request, ActorFactory.FromPrincipal(http.User), ct);
            return Results.Created($"/api/documents/{id}?revision={revised.Revision.RevisionNumber}", revised);
        }).RequireAuthorization(RolePolicies.Administrator);

        docs.MapGet("/{id:guid}/revisions", async (Guid id, HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.GetRevisionHistoryAsync(id, ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        docs.MapGet("/{id:guid}/revisions/verify", async (Guid id, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.VerifyRevisionsAsync(id, ct)))
            .RequireAuthorization(RolePolicies.Reviewer);

        docs.MapPost("/{id:guid}/distributions", async (Guid id, DistributeDocumentRequest request, HttpContext http, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.DistributeAsync(id, request, ActorFactory.FromPrincipal(http.User), ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        docs.MapGet("/{id:guid}/access-log", async (Guid id, int? skip, int? take, ISystemDocumentService service, CancellationToken ct) =>
            Results.Ok(await service.GetAccessLogAsync(id, skip ?? 0, take ?? 0, ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        return app;
    }
}

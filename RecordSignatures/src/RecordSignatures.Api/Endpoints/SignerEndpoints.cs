using AuthenticationCore.Shared.Authorization;
using RecordSignatures.Api.Security;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Api.Endpoints;

public static class SignerEndpoints
{
    public static IEndpointRouteBuilder MapSignerEndpoints(this IEndpointRouteBuilder app)
    {
        var signers = app.MapGroup("/api/signers").WithTags("Signers");

        signers.MapPost("", async (EnrollSignerRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
        {
            var signer = await service.EnrollAsync(request, OperatorFactory.FromHttpContext(http), ct);
            return Results.Created($"/api/signers/{Uri.EscapeDataString(signer.UserId)}", signer);
        }).RequireAuthorization(RolePolicies.Administrator);

        signers.MapGet("", async (string? status, bool? reviewDue, int? skip, int? take, ISignerService service, CancellationToken ct) =>
        {
            SignerStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<SignerStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                {
                    throw new SignatureValidationException($"Status must be one of: {string.Join(", ", Enum.GetNames<SignerStatus>())}.");
                }
                parsed = value;
            }
            return Results.Ok(await service.ListAsync(parsed, reviewDue ?? false, skip ?? 0, take ?? 0, ct));
        }).RequireAuthorization(RolePolicies.LabManager);

        signers.MapGet("/me", async (HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(OperatorFactory.FromHttpContext(http).UserId, ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        signers.MapPost("/me/certification", async (CertifyRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.AcceptCertificationAsync(request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.LabTechnician);

        signers.MapGet("/{userId}", async (string userId, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(userId, ct)))
            .RequireAuthorization(RolePolicies.LabManager);

        signers.MapGet("/{userId}/certification", async (string userId, ISignerService service, CancellationToken ct) =>
            Results.Text(await service.GetCertificationAsync(userId, ct), "text/plain; charset=utf-8"))
            .RequireAuthorization(RolePolicies.LabManager);

        signers.MapPost("/{userId}/suspend", async (string userId, SuspendSignerRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.SuspendAsync(userId, request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.Administrator);

        signers.MapPost("/{userId}/reinstate", async (string userId, ReinstateSignerRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.ReinstateAsync(userId, request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.Administrator);

        signers.MapPost("/{userId}/revoke", async (string userId, RevokeSignerRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.RevokeAsync(userId, request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.Administrator);

        signers.MapPost("/{userId}/review", async (string userId, ReviewSignerRequest request, HttpContext http, ISignerService service, CancellationToken ct) =>
            Results.Ok(await service.ReviewAsync(userId, request, OperatorFactory.FromHttpContext(http), ct)))
            .RequireAuthorization(RolePolicies.Administrator);

        return app;
    }
}

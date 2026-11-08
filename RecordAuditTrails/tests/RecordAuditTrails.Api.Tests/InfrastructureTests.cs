using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AuthenticationCore.Shared.Roles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using RecordAuditTrails.Api.Endpoints;
using RecordAuditTrails.Api.ErrorHandling;
using RecordAuditTrails.Api.Security;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Api.Tests;

public class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData(typeof(AuditValidationException), 400)]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(AccessDeniedException), 403)]
    [InlineData(typeof(ConcurrencyConflictException), 409)]
    [InlineData(typeof(ImmutableDataException), 409)]
    [InlineData(typeof(InvalidOperationException), 500)]
    public async Task MapsExceptionsToProblemDetails(Type exceptionType, int expectedStatus)
    {
        var exception = (Exception)(exceptionType.IsSubclassOf(typeof(AuditTrailException))
            ? Activator.CreateInstance(exceptionType, "visible message")!
            : new InvalidOperationException("secret connection string"));
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var handled = await new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance).TryHandleAsync(context, exception, default);

        Assert.True(handled);
        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expectedStatus, problem!.Status);
        if (expectedStatus == 500)
        {
            Assert.DoesNotContain("secret", problem.Detail);
        }
        else
        {
            Assert.Equal("visible message", problem.Detail);
        }
    }
}

public class ActorFactoryTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void BuildsActorFromClaimsUsingTheHighestRole()
    {
        var actor = ActorFactory.FromPrincipal(Principal(("sub", "42"), ("preferred_username", "ann"), ("role", "Reviewer"), ("role", "LabManager")));
        Assert.Equal(new Services.Actor("42", "ann", UserRole.LabManager), actor);
    }

    [Theory]
    [InlineData("", "ann", "Reviewer")]
    [InlineData("42", "", "Reviewer")]
    [InlineData("42", "ann", "NotARole")]
    public void RejectsPrincipalsMissingIdentityOrRole(string sub, string user, string role)
    {
        var claims = new List<(string, string)>();
        if (sub.Length > 0) claims.Add(("sub", sub));
        if (user.Length > 0) claims.Add(("preferred_username", user));
        claims.Add(("role", role));
        Assert.Throws<AccessDeniedException>(() => ActorFactory.FromPrincipal(Principal(claims.ToArray())));
    }
}

public class AuditQueryParametersTests
{
    [Fact]
    public void ConvertsOffsetsToUtc()
    {
        var from = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2));
        var query = new AuditQueryParameters("bob", AuditAction.Delete, "pH", from, null, 3, 4).ToQuery();
        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), query.FromUtc);
        Assert.Null(query.ToUtc);
        Assert.Equal(("bob", AuditAction.Delete, "pH", 3, 4), (query.Username, query.Action, query.FieldName, query.Skip, query.Take));
    }
}

public class StartupTests
{
    [Fact]
    public async Task HealthEndpointReportsHealthy()
    {
        using var factory = new ApiFactory();
        var body = await (await factory.CreateClient().GetAsync("/health")).JsonAsync();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SwaggerCanBeDisabledByConfiguration()
    {
        using var factory = new ApiFactory(new() { ["Swagger:Enabled"] = "false" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.yaml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public void MissingAuthenticationConfigurationStopsStartup()
    {
        using var factory = new ApiFactory(new() { ["AuthenticationCore:PublicKeyPem"] = "", ["AuthenticationCore:JwksUrl"] = "" });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void MigrateOnStartupRunsAgainstTheConfiguredDatabase()
    {
        // Real Npgsql provider pointing at nothing: proves the migration step is wired into startup and fails loudly.
        using var factory = new ApiFactory(new() { ["Database:MigrateOnStartup"] = "true" }, useSqlite: false);
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task MissingConnectionStringFailsRequestsWithoutLeakingDetails()
    {
        using var factory = new ApiFactory(new() { ["ConnectionStrings:AuditDb"] = "" }, useSqlite: false);

        var response = await factory.Technician().GetAsync("/api/records");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.JsonAsync();
        Assert.DoesNotContain("AuditDb", problem.GetProperty("detail").GetString());
    }
}

using AuthenticationCore.Shared.Authorization;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Validation;

var builder = WebApplication.CreateBuilder(args);

// All that an API needs: keys and revocations are cached locally and refreshed in the background.
builder.Services.AddAuthenticationCore(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/samples/public", () => Results.Ok(new { message = "Anyone can read this." }))
    .AllowAnonymous();

app.MapGet("/samples/whoami", (HttpContext http) => Results.Ok(new
{
    subject = http.User.GetSubject(),
    username = http.User.GetUsername(),
    roles = http.User.GetRoles(),
    highestRole = http.User.GetHighestRole()?.ToString(),
})).RequireAuthorization();

app.MapGet("/samples/technician", () => Results.Ok(new { message = "Lab Technician or higher." }))
    .RequireAuthorization(RolePolicies.LabTechnician);

app.MapGet("/samples/reviewer", () => Results.Ok(new { message = "Reviewer or higher." }))
    .RequireAuthorization(RolePolicies.Reviewer);

app.MapGet("/samples/manager", () => Results.Ok(new { message = "Lab Manager or higher." }))
    .RequireAuthorization(RolePolicies.LabManager);

app.MapGet("/samples/admin", () => Results.Ok(new { message = "Administrators only." }))
    .RequireAuthorization(RolePolicies.Administrator);

app.Run();

namespace AuthenticationCore.SampleApi
{
    /// <summary>Entry point marker for integration tests.</summary>
    public sealed class SampleApiMarker;
}

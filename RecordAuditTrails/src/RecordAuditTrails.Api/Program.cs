using System.Text.Json.Serialization;
using AuthenticationCore.Shared.Validation;
using RecordAuditTrails.Api.Endpoints;
using RecordAuditTrails.Api.ErrorHandling;
using RecordAuditTrails.Database;
using RecordAuditTrails.Services;

var builder = WebApplication.CreateBuilder(args);

// Tokens issued by the AuthenticationCore service are validated locally (signature, issuer, audience, lifetime, revocation).
builder.Services.AddAuthenticationCore(builder.Configuration);

builder.Services.AddAuditDatabase(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddAuditServices(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await app.Services.MigrateAuditDatabaseAsync();

    // Used by the container's init step: apply migrations, then exit without serving.
    if (app.Configuration.GetValue("Database:MigrateOnly", false))
    {
        return;
    }
}

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.MapApiDocumentation();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapRecordEndpoints();
app.MapDocumentEndpoints();

app.Run();

namespace RecordAuditTrails.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public sealed class ApiMarker;
}

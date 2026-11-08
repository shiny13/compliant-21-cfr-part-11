using System.Text.Json.Serialization;
using AuthenticationCore.Shared.Validation;
using RecordSignatures.Api.Endpoints;
using RecordSignatures.Api.ErrorHandling;
using RecordSignatures.Core;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Database;

var builder = WebApplication.CreateBuilder(args);

// Users log in at the AuthenticationCore service; its tokens are validated locally (signature, issuer, audience, lifetime, revocation).
builder.Services.AddAuthenticationCore(builder.Configuration);

builder.Services.AddSignaturesDatabase(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseServiceCollectionExtensions.ConnectionStringName));
builder.Services.AddRecordSignatures(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Fail fast on a missing or weak signature seal key rather than at the first signing.
_ = app.Services.GetRequiredService<SignatureSealer>();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await app.Services.MigrateSignaturesDatabaseAsync();
}

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.MapApiDocumentation();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous().ExcludeFromDescription();
app.MapSignerEndpoints();
app.MapSignatureEndpoints();

app.Run();

namespace RecordSignatures.Api
{
    /// <summary>Entry point marker for integration tests.</summary>
    public sealed class ApiMarker;
}

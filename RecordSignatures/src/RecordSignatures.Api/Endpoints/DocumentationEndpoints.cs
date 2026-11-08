using Swashbuckle.AspNetCore.SwaggerUI;

namespace RecordSignatures.Api.Endpoints;

/// <summary>Serves the hand-maintained OpenAPI YAML and Swagger UI on top of it.</summary>
public static class DocumentationEndpoints
{
    public const string SpecPath = "/openapi/v1.yaml";
    public const string UiPrefix = "swagger";

    public static string ReadSpec()
    {
        using var stream = typeof(DocumentationEndpoints).Assembly.GetManifestResourceStream("openapi.yaml")
            ?? throw new InvalidOperationException("The embedded openapi.yaml resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        var spec = ReadSpec();
        app.MapGet(SpecPath, () => Results.Text(spec, "application/yaml; charset=utf-8")).AllowAnonymous().ExcludeFromDescription();
        app.UseSwaggerUI((SwaggerUIOptions o) =>
        {
            o.SwaggerEndpoint(SpecPath, "Record Signatures API v1");
            o.RoutePrefix = UiPrefix;
            o.DocumentTitle = "Record Signatures API";
        });
        return app;
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;
using RecordAuditTrails.Api.Endpoints;

namespace RecordAuditTrails.Api.Tests;

public class OpenApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static YamlMappingNode LoadSpec()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(DocumentationEndpoints.ReadSpec()));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Map(YamlNode node, string key) => (YamlMappingNode)((YamlMappingNode)node)[key];

    [Fact]
    public async Task SpecIsServedAsYamlAndIsAnOpenApi3Document()
    {
        var response = await fixture.Factory.CreateClient().GetAsync("/openapi/v1.yaml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/yaml", response.Content.Headers.ContentType!.ToString());
        var served = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("openapi: 3.0.3", served);
        Assert.Equal(DocumentationEndpoints.ReadSpec(), served);
        Assert.Equal("3.0.3", ((YamlScalarNode)LoadSpec()["openapi"]).Value);
    }

    [Fact]
    public async Task SwaggerUiIsServedAndPointsAtTheYamlSpec()
    {
        var client = fixture.Factory.CreateClient();
        var page = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var config = await client.GetAsync("/swagger/index.js");
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        Assert.Contains("/openapi/v1.yaml", (await config.Content.ReadAsStringAsync()).Replace("\\/", "/"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/swagger-ui-bundle.js")).StatusCode);
    }

    [Fact]
    public void SpecDeclaresBearerSecurityAndEveryReferenceResolves()
    {
        var spec = LoadSpec();
        var scheme = Map(Map(Map(spec, "components"), "securitySchemes"), "bearerAuth");
        Assert.Equal("bearer", ((YamlScalarNode)scheme["scheme"]).Value);
        Assert.Equal("JWT", ((YamlScalarNode)scheme["bearerFormat"]).Value);

        var refs = new List<string>();
        void Collect(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode m:
                    foreach (var (k, v) in m.Children)
                    {
                        if (k is YamlScalarNode { Value: "$ref" } && v is YamlScalarNode r) refs.Add(r.Value!);
                        Collect(v);
                    }
                    break;
                case YamlSequenceNode s:
                    s.Children.ToList().ForEach(Collect);
                    break;
            }
        }
        Collect(spec);

        Assert.NotEmpty(refs);
        foreach (var reference in refs)
        {
            Assert.StartsWith("#/", reference);
            YamlNode current = spec;
            foreach (var part in reference[2..].Split('/'))
            {
                current = ((YamlMappingNode)current)[part];
            }
        }
    }

    [Fact]
    public void SpecDocumentsExactlyTheEndpointsTheApiExposes()
    {
        var documented = new HashSet<string>();
        foreach (var (path, item) in Map(LoadSpec(), "paths").Children)
        {
            foreach (var (verb, _) in ((YamlMappingNode)item).Children)
            {
                var v = ((YamlScalarNode)verb).Value!;
                if (v != "parameters")
                {
                    documented.Add($"{v.ToUpperInvariant()} {((YamlScalarNode)path).Value}");
                }
            }
        }

        var implemented = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true || e.RoutePattern.RawText == "/health")
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api") || e.RoutePattern.RawText == "/health")
            .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Select(m => $"{m} {System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText!, @"\{(\w+):guid\}", "{$1}").TrimEnd('/')}"))
            .ToHashSet();

        Assert.Equal(implemented.Order(), documented.Order());
        Assert.True(documented.Count >= 20);
    }

    [Fact]
    public void EveryOperationHasSummaryTagAndAtLeastOneSuccessResponse()
    {
        foreach (var (_, item) in Map(LoadSpec(), "paths").Children)
        {
            foreach (var (verb, op) in ((YamlMappingNode)item).Children.Where(c => ((YamlScalarNode)c.Key).Value != "parameters"))
            {
                var operation = (YamlMappingNode)op;
                Assert.True(operation.Children.ContainsKey("summary"), $"{verb} lacks a summary");
                Assert.True(operation.Children.ContainsKey("tags"), $"{verb} lacks tags");
                var codes = ((YamlMappingNode)operation["responses"]).Children.Keys.Select(k => ((YamlScalarNode)k).Value!);
                Assert.Contains(codes, c => c.StartsWith('2'));
            }
        }
    }
}

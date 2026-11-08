using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RecordAuditTrails.Api.Tests;

internal static class HttpExtensions
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> SendJsonAsync(this HttpClient client, HttpMethod method, string url, object? body = null) =>
        client.SendAsync(new HttpRequestMessage(method, url)
        {
            Content = body is null ? null : JsonContent.Create(body),
        });

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object body) =>
        client.SendJsonAsync(HttpMethod.Post, url, body);

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body) =>
        client.SendJsonAsync(HttpMethod.Put, url, body);

    public static Task<HttpResponseMessage> DeleteJsonAsync(this HttpClient client, string url, object body) =>
        client.SendJsonAsync(HttpMethod.Delete, url, body);

    public static async Task<Guid> CreateRecordAsync(this HttpClient client, string type = "BatchRecord", string title = "Batch 1", object? fields = null)
    {
        var response = await client.PostJsonAsync("/api/records", new { recordType = type, title, fields = fields ?? new { pH = "7.0", operatorName = "tina" } });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    public static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");
}

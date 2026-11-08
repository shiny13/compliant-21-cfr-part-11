using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace RecordSignatures.Api.Tests;

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

    public static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    public static object SignBody(Guid recordId, int version = 1, string meaning = "Authorship", string username = "tina.tech",
        string password = ApiFactory.Password, string? comment = null) =>
        new { recordId, recordVersion = version, meaning, comment, username, password };
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Integration;

/// <summary>
/// Calls the RecordAuditTrails API with the caller's own access token, so its audit trail names the individual who acted.
/// If it cannot be reached the operation fails: nothing is signed or changed without an audit trail entry.
/// </summary>
public sealed class AuditTrailClient(HttpClient http, IOptions<SignatureOptions> options) : IAuditTrailClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecordSnapshot> GetRecordAsync(Guid recordId, string accessToken, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/records/{recordId:D}", null, accessToken, ct);
        var dto = await ReadAsync<RecordDto>(response, ct);
        return new RecordSnapshot(dto.Id, dto.RecordType, dto.Title, dto.Fields ?? new Dictionary<string, string?>(), dto.Version, dto.IsDeleted);
    }

    public async Task<Guid> CreateRecordAsync(
        string recordType, string title, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/records", new { recordType, title, fields, reason }, accessToken, ct);
        return (await ReadAsync<RecordDto>(response, ct)).Id;
    }

    public async Task UpdateRecordAsync(
        Guid recordId, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/records/{recordId:D}", new { fields, reason }, accessToken, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string accessToken, CancellationToken ct)
    {
        var baseUrl = (options.Value.AuditTrailBaseUrl ?? throw new DependencyUnavailableException("Signatures:AuditTrailBaseUrl is not configured.")).TrimEnd('/');
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("The audit trail service could not be reached; nothing was changed.", ex);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new DependencyUnavailableException("The audit trail service returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new DependencyUnavailableException("The audit trail service returned an unreadable response.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var detail = await ReadDetailAsync(response, ct);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new NotFoundException(detail ?? "The record was not found."),
            HttpStatusCode.BadRequest => new SignatureValidationException(detail ?? "The audit trail service rejected the request."),
            HttpStatusCode.Conflict => new ConflictException(detail ?? "The audit trail service reported a conflict."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ForbiddenException(detail ?? "The audit trail service denied access."),
            _ => new DependencyUnavailableException($"The audit trail service answered {(int)response.StatusCode}; nothing was changed."),
        };
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RecordDto(
        Guid Id, string RecordType, string Title, Dictionary<string, string?>? Fields, int Version, bool IsDeleted);
}

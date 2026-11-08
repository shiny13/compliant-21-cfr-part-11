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
/// Talks to the AuthenticationCore service. A signing's identification code and password are verified by logging in with
/// them there, so passwords never reach this service's storage and AuthenticationCore's own lockout also counts the
/// attempts. The throw-away session created by that login is revoked straight away.
/// </summary>
public sealed class AuthenticationCoreClient(HttpClient http, IOptions<SignatureOptions> options) : ICredentialVerifier, ISessionRevoker
{
    public async Task<CredentialResult> VerifyAsync(string username, string password, CancellationToken ct = default)
    {
        var baseUrl = Base();
        try
        {
            using var login = await http.PostAsJsonAsync(baseUrl + "/auth/login", new { username, password }, ct);
            if (login.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                return CredentialResult.Rejected;
            }
            if (!login.IsSuccessStatusCode)
            {
                throw new DependencyUnavailableException($"The authentication service answered {(int)login.StatusCode}.");
            }

            var tokens = await login.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new DependencyUnavailableException("The authentication service returned an unreadable login response.");
            var subject = await GetSubjectAsync(baseUrl, tokens.AccessToken, ct);
            await TryLogoutAsync(baseUrl, tokens, ct);
            return subject is null ? CredentialResult.Rejected : CredentialResult.Accepted(subject);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            throw new DependencyUnavailableException("The authentication service could not be reached.", ex);
        }
    }

    public async Task<bool> RevokeUserSessionsAsync(string userId, string administratorAccessToken, CancellationToken ct = default)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            return false;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Base() + "/auth/revoke") { Content = JsonContent.Create(new { userId = id }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", administratorAccessToken);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<string?> GetSubjectAsync(string baseUrl, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(ct);
        return me?.Subject;
    }

    private async Task TryLogoutAsync(string baseUrl, TokenResponse tokens, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/auth/logout")
            {
                Content = JsonContent.Create(new { refreshToken = tokens.RefreshToken }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            using var _ = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The verification session expires on its own within minutes; failing to end it early is not an error.
        }
    }

    private string Base() =>
        (options.Value.AuthServiceBaseUrl ?? throw new DependencyUnavailableException("Signatures:AuthServiceBaseUrl is not configured.")).TrimEnd('/');

    private sealed record TokenResponse(string AccessToken, string RefreshToken);

    private sealed record MeResponse(string? Subject);
}

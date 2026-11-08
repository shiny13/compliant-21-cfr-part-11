using System.Net;
using System.Text;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Integration;

namespace RecordSignatures.Core.Tests;

public class AuthenticationCoreClientTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AuthenticationCoreClient Client(StubHandler handler, Action<RecordSignatures.Core.Models.SignatureOptions>? configure = null) =>
        new(new HttpClient(handler), TestOptions.Wrap(configure));

    /// <summary>Plays the real AuthenticationCore: login, me, logout.</summary>
    private static StubHandler HappyAuth(string subject = "id-tina") => new(r => r.RequestUri!.AbsolutePath switch
    {
        "/auth/login" => Json(HttpStatusCode.OK, """{"accessToken":"AT","tokenType":"Bearer","expiresIn":300,"refreshToken":"RT"}"""),
        "/auth/me" => Json(HttpStatusCode.OK, $$"""{"subject":"{{subject}}","username":"tina"}"""),
        "/auth/logout" => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Verify_LogsIn_ReadsTheSubject_AndEndsTheThrowawaySession()
    {
        var handler = HappyAuth();
        var result = await Client(handler).VerifyAsync("tina", "pw");

        Assert.True(result.Verified);
        Assert.Equal("id-tina", result.Subject);
        Assert.Equal(["/auth/login", "/auth/me", "/auth/logout"], handler.Requests.Select(r => new Uri(r.Uri).AbsolutePath));
        Assert.Contains("\"username\":\"tina\"", handler.Requests[0].Body);
        Assert.Equal("Bearer AT", handler.Requests[1].Authorization);
        Assert.Equal("Bearer AT", handler.Requests[2].Authorization);
        Assert.Contains("RT", handler.Requests[2].Body);
    }

    [Fact]
    public async Task Verify_TrimsATrailingSlashInTheBaseUrl()
    {
        var handler = HappyAuth();
        await Client(handler, o => o.AuthServiceBaseUrl = "http://auth.test///").VerifyAsync("tina", "pw");
        Assert.StartsWith("http://auth.test/auth/login", handler.Requests[0].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Verify_RejectedCredentials_AreRejected(HttpStatusCode code)
    {
        var handler = new StubHandler(_ => Json(code, "{}"));
        var result = await Client(handler).VerifyAsync("tina", "bad");
        Assert.False(result.Verified);
        Assert.Null(result.Subject);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Verify_ServerError_FailsClosed()
    {
        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))).VerifyAsync("tina", "pw"));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Verify_NetworkFailure_FailsClosed()
    {
        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => throw new HttpRequestException("refused"))).VerifyAsync("tina", "pw"));
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Verify_UnreadableLoginResponse_FailsClosed()
    {
        await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => Json(HttpStatusCode.OK, "not json"))).VerifyAsync("tina", "pw"));
        await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => Json(HttpStatusCode.OK, "null"))).VerifyAsync("tina", "pw"));
    }

    [Fact]
    public async Task Verify_WhenTheSubjectCannotBeRead_CredentialsAreNotAccepted()
    {
        var failing = new StubHandler(r => r.RequestUri!.AbsolutePath == "/auth/login"
            ? Json(HttpStatusCode.OK, """{"accessToken":"AT","refreshToken":"RT"}""")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.False((await Client(failing).VerifyAsync("tina", "pw")).Verified);

        var empty = new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/auth/login" => Json(HttpStatusCode.OK, """{"accessToken":"AT","refreshToken":"RT"}"""),
            "/auth/me" => Json(HttpStatusCode.OK, "null"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        Assert.False((await Client(empty).VerifyAsync("tina", "pw")).Verified);
    }

    [Fact]
    public async Task Verify_AFailedLogout_IsIgnored()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/auth/login" => Json(HttpStatusCode.OK, """{"accessToken":"AT","refreshToken":"RT"}"""),
            "/auth/me" => Json(HttpStatusCode.OK, """{"subject":"id-tina"}"""),
            _ => throw new HttpRequestException("logout failed"),
        });
        Assert.True((await Client(handler).VerifyAsync("tina", "pw")).Verified);
    }

    [Fact]
    public async Task Verify_WithoutABaseUrl_FailsClosed()
    {
        await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(HappyAuth(), o => o.AuthServiceBaseUrl = null).VerifyAsync("tina", "pw"));
    }

    [Fact]
    public async Task Verify_CallerCancellationIsNotReportedAsAnOutage()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(HappyAuth()).VerifyAsync("tina", "pw", cts.Token));
    }

    [Fact]
    public async Task RevokeSessions_PostsTheUserIdWithTheAdministratorsToken()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var id = Guid.NewGuid();
        Assert.True(await Client(handler).RevokeUserSessionsAsync(id.ToString(), "ADMIN-TOKEN"));
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/auth/revoke", request.Uri);
        Assert.Equal("Bearer ADMIN-TOKEN", request.Authorization);
        Assert.Contains(id.ToString(), request.Body);
    }

    [Fact]
    public async Task RevokeSessions_ReportsFailure_WithoutThrowing()
    {
        var id = Guid.NewGuid().ToString();
        Assert.False(await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))).RevokeUserSessionsAsync(id, "t"));
        Assert.False(await Client(new StubHandler(_ => throw new HttpRequestException("down"))).RevokeUserSessionsAsync(id, "t"));
        Assert.False(await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent))).RevokeUserSessionsAsync("not-a-guid", "t"));
    }
}

public class AuditTrailClientTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static AuditTrailClient Client(StubHandler handler, Action<RecordSignatures.Core.Models.SignatureOptions>? configure = null) =>
        new(new HttpClient(handler), TestOptions.Wrap(configure));

    private static readonly Guid Id = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task GetRecord_SendsTheCallersTokenAndParsesTheRecord()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"id":"{{Id}}","recordType":"BatchRecord","title":"Batch 7","fields":{"pH":"7.0","note":null},"version":3,"isDeleted":false,"createdBy":"x"}"""));
        var record = await Client(handler).GetRecordAsync(Id, "USER-TOKEN");

        Assert.Equal(Id, record.Id);
        Assert.Equal("BatchRecord", record.RecordType);
        Assert.Equal("Batch 7", record.Title);
        Assert.Equal(3, record.Version);
        Assert.False(record.IsDeleted);
        Assert.Equal("7.0", record.Fields["pH"]);
        Assert.Null(record.Fields["note"]);
        Assert.Equal("Bearer USER-TOKEN", handler.Requests.Single().Authorization);
        Assert.Equal($"http://audit.test/api/records/{Id}", handler.Requests.Single().Uri);
    }

    [Fact]
    public async Task GetRecord_MissingFieldsBecomeAnEmptyDictionary()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""{"id":"{{Id}}","recordType":"T","title":"x","version":1,"isDeleted":true}"""));
        var record = await Client(handler).GetRecordAsync(Id, "t");
        Assert.Empty(record.Fields);
        Assert.True(record.IsDeleted);
    }

    [Fact]
    public async Task CreateRecord_PostsTheDocumentAndReturnsTheNewId()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Created, $$"""{"id":"{{Id}}","recordType":"X","title":"t","version":1,"isDeleted":false}"""));
        var id = await Client(handler).CreateRecordAsync("ElectronicSignature", "Sig", new Dictionary<string, string?> { ["a"] = "b" }, "because", "tok");

        Assert.Equal(Id, id);
        var request = handler.Requests.Single();
        Assert.Equal("POST", request.Method);
        Assert.EndsWith("/api/records", request.Uri);
        Assert.Contains("\"recordType\":\"ElectronicSignature\"", request.Body);
        Assert.Contains("\"reason\":\"because\"", request.Body);
        Assert.Contains("\"a\":\"b\"", request.Body);
    }

    [Fact]
    public async Task UpdateRecord_PutsTheFieldsAndReason()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        await Client(handler).UpdateRecordAsync(Id, new Dictionary<string, string?> { ["status"] = "Revoked" }, "why not", "tok");
        var request = handler.Requests.Single();
        Assert.Equal("PUT", request.Method);
        Assert.EndsWith($"/api/records/{Id}", request.Uri);
        Assert.Contains("\"reason\":\"why not\"", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, typeof(NotFoundException))]
    [InlineData(HttpStatusCode.BadRequest, typeof(SignatureValidationException))]
    [InlineData(HttpStatusCode.Conflict, typeof(ConflictException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(ForbiddenException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(ForbiddenException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(DependencyUnavailableException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(DependencyUnavailableException))]
    public async Task ErrorStatuses_MapToBusinessExceptions(HttpStatusCode status, Type expected)
    {
        var handler = new StubHandler(_ => Json(status, """{"detail":"upstream says no"}"""));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Client(handler).GetRecordAsync(Id, "t"));
        Assert.IsType(expected, ex);
        if (expected != typeof(DependencyUnavailableException))
        {
            Assert.Equal("upstream says no", ex.Message);
        }
    }

    [Fact]
    public async Task ErrorWithoutAReadableBody_UsesADefaultMessage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("plain text") });
        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Client(handler).GetRecordAsync(Id, "t"));
        Assert.Contains("not found", ex.Message);

        var noDetail = new StubHandler(_ => Json(HttpStatusCode.BadRequest, """{"title":"x"}"""));
        var ex2 = await Assert.ThrowsAsync<SignatureValidationException>(() => Client(noDetail).UpdateRecordAsync(Id, new Dictionary<string, string?>(), "r", "t"));
        Assert.Contains("rejected", ex2.Message);
    }

    [Fact]
    public async Task NetworkFailure_MeansNothingWasChanged()
    {
        var ex = await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => throw new HttpRequestException("refused"))).GetRecordAsync(Id, "t"));
        Assert.Contains("nothing was changed", ex.Message);
    }

    [Fact]
    public async Task UnreadableSuccessBody_FailsClosed()
    {
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => Client(new StubHandler(_ => Json(HttpStatusCode.OK, "{broken"))).GetRecordAsync(Id, "t"));
        await Assert.ThrowsAsync<DependencyUnavailableException>(() => Client(new StubHandler(_ => Json(HttpStatusCode.OK, "null"))).GetRecordAsync(Id, "t"));
    }

    [Fact]
    public async Task WithoutABaseUrl_FailsClosed()
    {
        await Assert.ThrowsAsync<DependencyUnavailableException>(() =>
            Client(new StubHandler(_ => Json(HttpStatusCode.OK, "{}")), o => o.AuditTrailBaseUrl = null).GetRecordAsync(Id, "t"));
    }
}

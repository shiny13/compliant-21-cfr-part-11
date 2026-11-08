using AuthenticationCore.Shared.Roles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

/// <summary>The real services wired to in-memory stores and scriptable fakes for AuthenticationCore and RecordAuditTrails.</summary>
public sealed class Harness
{
    public static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public TestClock Clock { get; } = new(Start);
    public InMemoryData Data { get; } = new();
    public FakeAuditTrail Audit { get; } = new();
    public FakeCredentialVerifier Auth { get; } = new();
    public CollectingAlertSink Sink { get; } = new();
    public InMemoryUnitOfWork Uow { get; }
    public SignatureOptions Options { get; }
    public SignatureSealer Sealer { get; }
    public SecurityAlertService Alerts { get; }
    public CredentialChallenge Challenge { get; }
    public SignerService Signers { get; }
    public SignatureService Signatures { get; }
    public SignatureReportService Reports { get; }
    public InMemorySignerStore SignerStore { get; }
    public InMemorySignatureStore SignatureStore { get; }
    public InMemorySecurityStore SecurityStore { get; }

    public Operator Admin { get; } = new("id-adam", "adam.admin", UserRole.Administrator, "tok-adam");
    public Operator Manager { get; } = new("id-mia", "mia.manager", UserRole.LabManager, "tok-mia");
    public Operator Reviewer { get; } = new("id-rob", "rob.reviewer", UserRole.Reviewer, "tok-rob");
    public Operator Tech { get; } = new("id-tina", "tina.tech", UserRole.LabTechnician, "tok-tina");

    public const string Password = "Correct-Horse-1!";

    public Harness(Action<SignatureOptions>? configure = null)
    {
        Options = TestOptions.Create(configure);
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        Uow = new InMemoryUnitOfWork(Data);
        SignerStore = new InMemorySignerStore(Data);
        SignatureStore = new InMemorySignatureStore(Data);
        SecurityStore = new InMemorySecurityStore(Data);
        Sealer = new SignatureSealer(options);
        Alerts = new SecurityAlertService(SecurityStore, [Sink], Clock, NullLogger<SecurityAlertService>.Instance);
        Challenge = new CredentialChallenge(Auth, SecurityStore, Alerts, options, Clock);
        Signers = new SignerService(SignerStore, Uow, Audit, Challenge, Auth, Alerts, options, Clock, NullLogger<SignerService>.Instance);
        Signatures = new SignatureService(SignerStore, SignatureStore, Uow, Audit, Challenge, Alerts, Sealer, options, Clock, NullLogger<SignatureService>.Instance);
        Reports = new SignatureReportService(Signatures, Clock);

        foreach (var user in new[] { Admin, Manager, Reviewer, Tech })
        {
            Auth.Add(user.Username, Password, user.UserId);
        }
    }

    public static IdentityVerificationRequest Identity { get; } = new(IdentityVerificationMethod.GovernmentIssuedId, "Passport sighted by HR, ticket HR-1");

    public static EnrollSignerRequest EnrollRequest(Operator user, string? printedName = null, string? employeeId = null) =>
        new(user.UserId, user.Username, printedName ?? $"Printed {user.Username}", employeeId ?? $"E-{user.UserId}", "Analyst", Identity);

    /// <summary>Enrolls (by the administrator) and certifies (by the person) so the individual can sign.</summary>
    public async Task<Signer> ActivateAsync(Operator user, string? printedName = null)
    {
        await Signers.EnrollAsync(EnrollRequest(user, printedName), Admin);
        return await Signers.AcceptCertificationAsync(new CertifyRequest(user.Username, Password, CertificationStatement.CurrentVersion), user);
    }

    public SignRequest Sign(Guid recordId, int version = 1, string meaning = MeaningCatalog.Authorship, Operator? who = null, string? password = null, string? comment = null) =>
        new(recordId, version, meaning, comment, (who ?? Tech).Username, password ?? Password);
}

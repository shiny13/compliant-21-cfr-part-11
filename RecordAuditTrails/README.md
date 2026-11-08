# RecordAuditTrails

Electronic record management with a secure, computer-generated, time-stamped **audit trail**, built around the closed-system controls of **21 CFR Part 11.10**. Users sign in through the [AuthenticationCore](../AuthenticationCore/README.md) service and call this API with the access token it issues.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `src/RecordAuditTrails.Models` | class library | Data model layer: `ElectronicRecord`, `AuditTrailEntry`, `AccessLogEntry`, `SystemDocument`, `DocumentRevision`. No dependencies. |
| `src/RecordAuditTrails.Core` | class library | Core logic: hash chain + verification, change detection, reason validation, retention policy, field serialization, report formatters (JSON, CSV, text, HTML), domain exceptions. No database or web code. |
| `src/RecordAuditTrails.Database` | class library | Database connection project: EF Core `AuditDbContext` for **PostgreSQL** (Npgsql), repositories, unit of work, migrations, immutability guard. |
| `src/RecordAuditTrails.Services` | class library | Service classes that perform the actions: `RecordService`, `AuditTrailService`, `RecordExportService`, `RetentionService`, `SystemDocumentService`, `AuditTrailWriter`. |
| `src/RecordAuditTrails.Api` | ASP.NET Core | Minimal API calling the services. Validates AuthenticationCore tokens statelessly. Hand-written `openapi.yaml` + Swagger UI. |
| `tests/*.Tests` | xUnit | One test project per layer. |

Dependency direction: `Models ← Core ← Database ← Services ← Api`.

## How each Part 11 requirement is met

| Requirement | Implementation |
|---|---|
| **11.10(e)** secure, computer-generated, time-stamped audit trail of create / modify / delete | Every operation writes one `AuditTrailEntry` **per changed field**, in the same database transaction as the record change. Timestamps come from the server clock (`TimeProvider`, UTC), never from the request. The operator (`sub`, `preferred_username`, role) is taken only from the validated token. |
| Changes must not obscure previous values | Entries are append-only and hold `OldValue`, `NewValue`, user, UTC time and `Reason`. Modify and delete **require a reason** (min 5 chars). Delete is logical (`IsDeleted`); records are never physically removed. |
| Tamper evidence | Entries form a per-record SHA-256 hash chain (gap-free `Sequence`, `PreviousHash`, `Hash`). `GET .../audit-trail/verify` and `GET /api/audit-trail/verify` recompute the chains and report the first broken entry. A unique `(RecordId, Sequence)` index stops concurrent writers from forking a chain. |
| Append-only enforcement | Two layers: an EF `SaveChangesInterceptor` rejects updates/deletes of audit, access-log and revision rows; **PostgreSQL triggers** (migration `AddImmutabilityTriggers`) block `UPDATE`, `DELETE` and `TRUNCATE` on those tables and `DELETE`/`TRUNCATE` on records and documents, even for direct SQL through the application role. |
| Retention at least as long as the record, available to FDA | `RetainUntilUtc` is set at creation from `Retention:ByRecordType` / `Retention:DefaultYears`. The audit trail inherits that date, is never purged by the system, and stays readable for deleted records. `GET /api/retention/expired` lists disposition candidates; nothing is deleted automatically. |
| **11.10(b)** accurate, complete copies in human-readable and electronic form | `GET /api/records/{id}/export?format=json\|csv\|text\|html` returns the record, its full audit trail and an integrity verdict, with an `X-Content-SHA256` digest. HTML output is encoded and CSV neutralizes formula injection. Each export is logged. |
| **11.10(d/g)** access limited to authorized individuals | AuthenticationCore role policies on every endpoint (see matrix). The role hierarchy is "this role or higher". |
| **11.10(k)(1)** control over distribution of, access to and use of system documentation | `SystemDocument.MinimumReadRole` restricts who can read a document; every view and every distribution (recipient + revision) is written to the append-only `access_log`, readable by Lab Managers. |
| **11.10(k)(2)** revision and change control for system documentation | Each change adds an immutable, hash-chained `DocumentRevision` with author, UTC time and a mandatory change description. The full time-sequenced history is retrievable and verifiable. |

## Roles

| Endpoint | Minimum role |
|---|---|
| Create / read / list / modify / delete records | LabTechnician |
| Record audit trail, verify, export, retention status, search all audit entries | Reviewer |
| Verify all audit trails, list retention-expired records | LabManager |
| List/read documents (also limited by the document's own minimum role) | LabTechnician |
| Verify document revisions | Reviewer |
| Log a distribution, read a document's access log | LabManager |
| Create documents, add revisions | Administrator |

## API documentation

* OpenAPI document: `GET /openapi/v1.yaml` (source: `src/RecordAuditTrails.Api/OpenApi/openapi.yaml`, embedded in the assembly)
* Swagger UI: `/swagger` (use **Authorize** and paste the access token)
* A test asserts the YAML documents exactly the endpoints the API maps, and that every `$ref` resolves, so the file cannot silently drift.
* Disable both with `Swagger:Enabled=false`.

## Run it

```bash
cd RecordAuditTrails
docker compose up -d                                   # PostgreSQL on :5432
# in ../AuthenticationCore: dotnet run --project src/AuthenticationCore.Service --launch-profile http   (:5194)
dotnet run --project src/RecordAuditTrails.Api --launch-profile http                                   # :5196, applies migrations in Development
```

```bash
TOKEN=$(curl -s localhost:5194/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"technician","password":"Dev-Technician-Passw0rd!"}' | jq -r .accessToken)

curl -s localhost:5196/api/records -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"recordType":"BatchRecord","title":"Batch 7","fields":{"pH":"7.0"}}'
```

Configuration (`appsettings.json`): `ConnectionStrings:AuditDb`, `AuthenticationCore:*` (issuer, audience, `JwksUrl`, revocation feed), `Retention:*`, `Database:MigrateOnStartup`, `Swagger:Enabled`.

## Container and Kubernetes

```bash
RecordAuditTrails/scripts/build-image.sh         # image part11/record-audit-trails:local (build context: the repository root)
RecordAuditTrails/scripts/test.sh --coverage     # tests (set AUDIT_TEST_PG for the PostgreSQL ones)
RecordAuditTrails/scripts/helm-lint.sh           # lint this chart
RecordAuditTrails/scripts/deploy.sh -n NS -r REL -f my-values.yaml      # install this service alone
```

* `Dockerfile`: multi-stage, chiseled non-root runtime image, port 8080, `GET /health` for probes.
* Chart: `helm/record-audit-trails`; the whole platform (with PostgreSQL) is `deploy/helm/part11-platform`. Guide and configuration reference: [../deploy/README.md](../deploy/README.md).
* Configure with environment variables: `ConnectionStrings__AuditDb`, `AuthenticationCore__*`, `Retention__DefaultYears`, `Retention__ByRecordType__<Type>`, `Swagger__Enabled`. The Secret needs `ConnectionStrings__AuditDb` and `AuthenticationCore__Revocation__FeedServiceKey`.
* **Migrations:** `Database__MigrateOnStartup=true` applies them at start. `Database__MigrateOnly=true` (with it) applies them and exits with status 0 without serving; the chart runs that in an init container so several replicas can start safely.
* Stateless: scale horizontally. All state is in PostgreSQL, which must be backed up for the whole retention period.
* Set `retention.byRecordType` for the record types **RecordSignatures** writes (`ElectronicSignature`, `SignerEnrollment`, `SecurityAlert`) to at least the longest signed record type; the chart defaults to 15 years.

## Tests

```bash
dotnet test                                              # PostgreSQL tests are skipped without the variable below
AUDIT_TEST_PG="Host=localhost;Username=audit_app;Password=dev-only-change-me;Database=postgres" dotnet test
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage"
```

* Core, Services, Database and API tests run on in-memory SQLite with the real repositories, services, endpoints and AuthenticationCore token validation (tokens signed with a test RSA key).
* With `AUDIT_TEST_PG` set (use `docker compose up -d` first), extra tests create throw-away databases, apply the real migrations, prove the triggers block tampering and that the hash chain survives PostgreSQL's microsecond timestamp storage, and run the API end to end on PostgreSQL.
* Line coverage across all five source projects is ~100% (generated EF migrations and the design-time factory are excluded in `coverage.runsettings`).

## Limitations (read before production use)

* **Electronic signatures (11.50 / 11.70) are out of scope.** The audit trail identifies the authenticated operator; it does not capture a signature meaning or re-authentication per action.
* **The hash chain is tamper-*evident*, not tamper-*proof*.** Someone able to rewrite a whole chain and its hashes can forge it. For stronger guarantees, periodically anchor the latest hashes in an external write-once store. A database owner can still drop triggers: use a least-privilege application role (no DDL, no `TRUNCATE`) and audit DBA access.
* Audit entries are not foreign-keyed to records on purpose, so the trail outlives any record row.
* Read access to records is not itself audit-logged (only exports and document access are).
* TLS, rate limiting and secrets management belong at the gateway / deployment layer. Connection strings and keys in `appsettings.Development.json` and `docker-compose.yml` are development placeholders only.
* Token revocation depends on AuthenticationCore's feed polling; see its README for the exposure window.
* Compliance depends on the whole system, procedures and validation, not on this code alone.

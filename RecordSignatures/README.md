# RecordSignatures

Electronic signatures for electronic records, built to meet **21 CFR Part 11** sections **11.50, 11.70, 11.100, 11.200 and 11.300**.

Users sign in at the [AuthenticationCore](../AuthenticationCore/README.md) service and call this API with the access token it issues. Records and every audit trail entry live in the [RecordAuditTrails](../RecordAuditTrails/README.md) API: RecordSignatures signs those records, and writes everything it does (enrollments, signatures, revocations, security alerts) to that audit trail under the acting person's own identity.

## Documentation

| Document | What it covers |
|---|---|
| **This README** | Overview, how to run, configuration, endpoints, roles, tests, limitations |
| [docs/PART11-COMPLIANCE.md](docs/PART11-COMPLIANCE.md) | **Clause by clause**: how each requirement of 11.50, 11.70, 11.100, 11.200 and 11.300 is addressed, where in the code, and what is out of scope |
| [docs/SIGNATURE-PROCEDURES.md](docs/SIGNATURE-PROCEDURES.md) | The operating procedures an organization needs around the software: enrollment, certification, loss management, periodic review, alerts, key management, inspections |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, data model, the sealing algorithm, the signing sequence, consistency and failure behavior, threat model |
| [docs/TRACEABILITY.md](docs/TRACEABILITY.md) | Requirement to implementation to automated test matrix, for validation |
| `src/RecordSignatures.Api/OpenApi/openapi.yaml` | OpenAPI 3 description, served at `/openapi/v1.yaml` with Swagger UI at `/swagger` |

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `src/RecordSignatures.Core` | class library | **All business logic.** Signer lifecycle, signing, sealing and verification, lockout, alerts, reports, and the clients for AuthenticationCore and RecordAuditTrails. No database or web code. |
| `src/RecordSignatures.Database` | class library | **Database layer.** EF Core entity models, `SignaturesDbContext` for **PostgreSQL** (Npgsql), stores, migrations, immutability guard and triggers. |
| `src/RecordSignatures.Api` | ASP.NET Core | Minimal API. Validates AuthenticationCore tokens locally, calls the Core services. Hand-written `openapi.yaml` and Swagger UI. |
| `tests/*.Tests` | xUnit | One test project per layer. |

Dependency direction: `AuthenticationCore.Shared ← Core ← Database ← Api`. Core defines the store interfaces; Database implements them and maps its entity models to the Core domain types.

## What it does

```
 user ──login──▶ AuthenticationCore ──▶ access token
   │
   │  POST /api/signatures  { recordId, recordVersion, meaning, username, password }
   ▼
 RecordSignatures.Api ──▶ checks the signer is enrolled, certified, current, and allowed this meaning
        │               ──▶ re-verifies username + password with AuthenticationCore (every time)
        │               ──▶ reads the record from RecordAuditTrails; the version must match what the signer saw
        │               ──▶ writes the signature into RecordAuditTrails (audit trail entry, signer's own token)
        └───────────────▶ stores the sealed signature in its own PostgreSQL database
```

A signature manifests the signer's **printed name**, the **UTC date and time** and the **meaning** (Authorship, Responsibility, Review, Approval), and is **sealed** (HMAC-SHA256) to one version and content hash of one record and to the previous signature on that record.

### Requirement summary

| Rule | How it is met (details in [docs/PART11-COMPLIANCE.md](docs/PART11-COMPLIANCE.md)) |
|---|---|
| **11.50** printed name, date/time, meaning | Stored in every signature, shown in every report (JSON, text, HTML), and recorded in the audit trail. Meaning comes from a closed list. |
| **11.70** signature linked to its record | HMAC seal over record id, version and content hash, plus a per-record chain. Copying, editing or removing a signature is detected. Append-only tables and database triggers. |
| **11.100(a)** unique, never reused | Unique user id and identification code (database indexes), one live enrollment per person, revoked identities never reassigned. |
| **11.100(b)** identity verified | Administrator records method and evidence at enrollment and at every re-verification. |
| **11.100(c)** certification | Each individual accepts the legal-equivalence statement after re-entering their credentials; a printable certification is available. |
| **11.200(a)** two components, genuine owner only | Identification code and password are required for every signing; the code must be the logged-in user's own; enrollment needs a second person. |
| **11.300** control of codes and passwords | Lockout, immediate security alerts, suspend / revoke with session cut-off, periodic re-verification. |

## Run it

```bash
cd RecordSignatures
docker compose up -d        # this API's PostgreSQL on :5433
# RecordAuditTrails needs its own PostgreSQL on :5432: (cd ../RecordAuditTrails && docker compose up -d)

# three terminals (or background them):
dotnet run --project ../AuthenticationCore/src/AuthenticationCore.Service --launch-profile http   # :5194
dotnet run --project ../RecordAuditTrails/src/RecordAuditTrails.Api --launch-profile http        # :5196
dotnet run --project src/RecordSignatures.Api --launch-profile http                              # :5197, applies migrations in Development
```

Swagger UI: <http://localhost:5197/swagger> (use **Authorize** and paste an access token).

A walk through, using the development users that AuthenticationCore seeds:

```bash
A=http://localhost:5194; AU=http://localhost:5196; SG=http://localhost:5197
login() { curl -s $A/auth/login -H 'Content-Type: application/json' -d "{\"username\":\"$1\",\"password\":\"$2\"}" | jq -r .accessToken; }
ADMIN=$(login admin 'Dev-Admin-Passw0rd!'); TECH=$(login technician 'Dev-Technician-Passw0rd!')
TECH_ID=$(curl -s $A/auth/me -H "Authorization: Bearer $TECH" | jq -r .subject)

# 1. An administrator verifies the technician's identity and enrolls them
curl -s $SG/api/signers -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' -d "{
  \"userId\":\"$TECH_ID\",\"username\":\"technician\",\"printedName\":\"Terry Technician\",\"employeeId\":\"EMP-0042\",
  \"identity\":{\"method\":\"GovernmentIssuedId\",\"evidenceReference\":\"Passport sighted by HR, ticket HR-1043\"}}"

# 2. The technician accepts the certification, re-entering their credentials
curl -s $SG/api/signers/me/certification -H "Authorization: Bearer $TECH" -H 'Content-Type: application/json' \
  -d '{"username":"technician","password":"Dev-Technician-Passw0rd!","statementVersion":"1.0"}'

# 3. A record exists in RecordAuditTrails; the technician signs version 1 of it
REC=$(curl -s $AU/api/records -H "Authorization: Bearer $TECH" -H 'Content-Type: application/json' \
  -d '{"recordType":"BatchRecord","title":"Batch 42","fields":{"pH":"7.0"}}' | jq -r .id)
curl -s $SG/api/signatures -H "Authorization: Bearer $TECH" -H 'Content-Type: application/json' \
  -d "{\"recordId\":\"$REC\",\"recordVersion\":1,\"meaning\":\"Authorship\",\"username\":\"technician\",\"password\":\"Dev-Technician-Passw0rd!\"}"

# 4. A reviewer prints the signatures of the record
REV=$(login reviewer 'Dev-Reviewer-Passw0rd!')
curl -s "$SG/api/records/$REC/signatures/report?format=text" -H "Authorization: Bearer $REV"
```

## Configuration (`appsettings.json`)

| Key | Meaning |
|---|---|
| `ConnectionStrings:SignaturesDb` | PostgreSQL connection string (Development points at `docker-compose.yml`). |
| `Database:MigrateOnStartup` | Apply EF migrations at startup (on in Development only). |
| `AuthenticationCore:*` | Issuer, audience, `JwksUrl` and revocation feed, exactly as for any AuthenticationCore API. |
| `Signatures:AuthServiceBaseUrl` | AuthenticationCore service; used to verify signing credentials and to revoke sessions. |
| `Signatures:AuditTrailBaseUrl` | RecordAuditTrails API. |
| `Signatures:SealKeys` / `ActiveSealKeyId` | HMAC keys (id to base64, at least 32 bytes) and the id used for new signatures. **The API refuses to start without a valid active key.** Keep retired keys so old signatures still verify. |
| `Signatures:MaxFailedAttempts`, `FailureWindow`, `LockoutDuration` | Lockout after repeated failed signing credential checks (defaults 3 / 15 min / 15 min). |
| `Signatures:ReviewIntervalMonths` | How often each signer must be re-verified (default 12). |
| `Signatures:MaxCommentLength` | Default 500. |
| `Signatures:AlertWebhookUrl` | Optional URL that receives every security alert as JSON immediately. |
| `Signatures:OrganizationName` | Printed in the certification statement. |
| `Swagger:Enabled` | Serve `/openapi/v1.yaml` and `/swagger`. |

The seal key in `appsettings.Development.json` is a development placeholder. In production load it from a secret store, **not** from the same place as the database credentials.

### Retention: configure RecordAuditTrails too

Signatures, enrollments and alerts are never purged by this service. Their audit trail entries live in RecordAuditTrails, whose default retention is 7 years. Retention must be at least as long as that of the records being signed, so set RecordAuditTrails `Retention:ByRecordType` for the record types this service writes: `ElectronicSignature`, `SignerEnrollment` and `SecurityAlert`.

```json
"Retention": { "DefaultYears": 7, "ByRecordType": { "ElectronicSignature": 15, "SignerEnrollment": 15, "SecurityAlert": 15 } }
```

## Roles

| Endpoint | Minimum role |
|---|---|
| Enroll a signer; suspend, reinstate, revoke or review a signer | Administrator |
| List signers, read a signer, print a certification, read security alerts, revoke a signature | LabManager |
| Report on a record's signatures (`/report`) | Reviewer |
| Read your own enrollment, accept your certification, list meanings, sign, read signatures of a record | LabTechnician |

Signing additionally requires being an **active, certified, current** enrolled signer, and the role the chosen meaning needs: Authorship and Responsibility need LabTechnician, Review needs Reviewer, Approval needs LabManager.

An administrator can never enroll, suspend, reinstate, revoke or review **themself**: a second person must always be involved.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/signers` | Enroll a signer after identity verification |
| GET | `/api/signers?status=&reviewDue=` | List signers |
| GET | `/api/signers/me` | Your own enrollment |
| POST | `/api/signers/me/certification` | Accept the certification (re-enter credentials) |
| GET | `/api/signers/{userId}` | One signer |
| GET | `/api/signers/{userId}/certification` | Printable certification |
| POST | `/api/signers/{userId}/suspend` · `/reinstate` · `/revoke` · `/review` | Loss management and periodic review |
| GET | `/api/meanings` | The signature meanings and the role each needs |
| POST | `/api/signatures` | Sign a record |
| GET | `/api/signatures/{id}` | One signature with its verification status |
| POST | `/api/signatures/{id}/revoke` | Withdraw a signature (reason required) |
| GET | `/api/records/{recordId}/signatures` | All signatures on a record, verified |
| GET | `/api/records/{recordId}/signatures/report?format=json\|text\|html` | Human-readable and electronic report with `X-Content-SHA256` |
| GET | `/api/security-alerts` | Alerts for attempted unauthorized use of credentials |

Errors are RFC 7807 problem documents: `400` validation, `401` token missing/invalid **or** signing credentials not accepted, `403` not allowed, `404` not found, `409` conflict (duplicate, stale record version), `423` signing locked (`lockedUntilUtc`), `503` an upstream service is unreachable (nothing was changed).

## Container and Kubernetes

```bash
RecordSignatures/scripts/build-image.sh          # image part11/record-signatures:local (build context: the repository root)
RecordSignatures/scripts/test.sh --coverage      # tests (set SIGNATURES_TEST_PG for the PostgreSQL ones)
RecordSignatures/scripts/helm-lint.sh            # lint this chart
RecordSignatures/scripts/deploy.sh -n NS -r REL -f my-values.yaml       # install this service alone
```

* `Dockerfile`: multi-stage, chiseled non-root runtime image, port 8080, `GET /health` for probes.
* Chart: `helm/record-signatures`; the whole platform (with PostgreSQL and the other two services) is `deploy/helm/part11-platform`. Guide and configuration reference: [../deploy/README.md](../deploy/README.md). End-to-end check of a running platform: `scripts/smoke-test.sh`.
* Configure with environment variables: `ConnectionStrings__SignaturesDb`, `AuthenticationCore__*`, `Signatures__AuthServiceBaseUrl`, `Signatures__AuditTrailBaseUrl`, `Signatures__ActiveSealKeyId`, `Signatures__SealKeys__<id>`, and the policy settings above.
* **The seal keys are the most sensitive secret.** The Secret carries `Signatures__SealKeys__<id>`; back it up apart from the database and keep every retired key for the retention period ([procedure](docs/SIGNATURE-PROCEDURES.md#9-seal-key-management)). The service will not start without an active key.
* **Migrations:** `Database__MigrateOnStartup=true` applies them at start; adding `Database__MigrateOnly=true` applies them and exits without serving. The chart runs the second in an init container.
* Stateless apart from PostgreSQL: scale horizontally. It depends on AuthenticationCore and RecordAuditTrails being reachable; if either is down, signing fails closed (503).
* Run behind TLS: signing requests carry passwords.

## Tests

```bash
dotnet test                                          # PostgreSQL tests are skipped without the variable below
SIGNATURES_TEST_PG="Host=localhost;Port=5433;Username=signatures_app;Password=dev-only-change-me;Database=postgres" dotnet test
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage"
```

342 tests in total (329 run without PostgreSQL; the other 13 are skipped until `SIGNATURES_TEST_PG` is set).

* **Core.Tests** (204): every service and rule, using the real services over in-memory stores and scriptable fakes for AuthenticationCore and RecordAuditTrails; HTTP clients tested against stub handlers.
* **Database.Tests** (50): the real stores and immutability guard on in-memory SQLite; with `SIGNATURES_TEST_PG` set, also the real migrations on throw-away PostgreSQL databases: the triggers, the partial unique index, microsecond timestamp round trips, and a race for one chain position.
* **Api.Tests** (88): the real API in memory with real AuthenticationCore token validation (tokens signed with a test RSA key). `CrossServiceTests` host the **real RecordAuditTrails API** in the same process and assert what really lands in its audit trail. A test checks `openapi.yaml` documents exactly the routes the API maps.
* Line coverage of all three source projects is **100%** when `SIGNATURES_TEST_PG` is set (generated EF migrations and the design-time factory are excluded in `coverage.runsettings`). Without PostgreSQL, the migration runner and the Postgres-only paths are not exercised.

## Limitations (read before production use)

* **This software supports compliance, it does not create it.** Part 11 compliance depends on procedures, training, validation and the whole system. See [docs/SIGNATURE-PROCEDURES.md](docs/SIGNATURE-PROCEDURES.md). The 11.100(c) certification to FDA is a paper submission the organization makes.
* **Password aging and strength rules belong to AuthenticationCore**, which does not currently enforce them (11.300(b) periodic revision of passwords). This service adds periodic re-verification of the signer's *authority*, not password expiry.
* **The seal is tamper-*evident*.** Someone who holds both write access to the database *and* the seal key can forge signatures. Keep the key in a secret store apart from the database, and rotate it per [docs/SIGNATURE-PROCEDURES.md](docs/SIGNATURE-PROCEDURES.md). A database owner can also drop the triggers: use a least-privilege application role and audit DBA access.
* **Removing the newest signature(s) of a record cannot be detected by the chain alone** (nothing follows them). Triggers block deletes, and the RecordAuditTrails audit trail holds an independent record of each signing to compare against.
* **A signature's printed name is fixed at enrollment.** A legal name change is handled by revoking the enrollment and enrolling a new identity (a new account) with fresh identity verification.
* **Signature reports are separate from RecordAuditTrails exports.** A system that displays or prints a signed record must also show the signature report (11.50(b)); RecordAuditTrails' own exports do not embed signatures.
* **Hardware tokens, cards and biometrics are not used**, so 11.200(b) and 11.300(e) do not apply. Adding them would require new controls.
* Timestamps come from the server clock in UTC; keep servers synchronized (NTP).
* AuthenticationCore's own limitations apply: in-memory stores, ephemeral signing key unless configured, and revocation reaching APIs on its feed poll interval.
* TLS, rate limiting and secrets management belong at the gateway / deployment layer. Keys and passwords in `appsettings.Development.json` and `docker-compose.yml` are development placeholders only.

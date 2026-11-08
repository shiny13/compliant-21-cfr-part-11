# AuthenticationCore

A hybrid authentication solution for .NET: a **centralized auth service** that issues short-lived JWTs, plus a **shared library** that lets every API validate those tokens **statelessly and locally**, with no per-request call back to the auth service.

## Solution layout

| Project | Type | Purpose |
|---|---|---|
| `src/AuthenticationCore.Shared` | C# class library | Token validation, local revocation cache, role policies, gateway-to-service context tokens. Contains **no business logic**. |
| `src/AuthenticationCore.Service` | ASP.NET Core | The centralized auth service: login, refresh, logout, revocation, JWKS, revocation feed, audit events. |
| `src/AuthenticationCore.SampleApi` | ASP.NET Core | Example resource API that depends only on the shared library. |
| `tests/AuthenticationCore.Tests` | xUnit | Unit tests plus integration tests running the service and the API together in memory. |

## How it works

```
 client ──login──▶ AuthenticationCore.Service ──▶ access JWT (5 min, RS256) + refresh token (rotating)
    │                       │  ▲
    │                       │  └── background: JWKS (public keys), revocation feed
    ▼                       ▼
 API gateway ──validates JWT──▶ mints internal context token (30 s) ──▶ internal service
                                                                        (validates locally)
```

- **Stateless validation.** The auth service signs access tokens with an RSA private key that only it holds. APIs verify signature, issuer, audience and lifetime using cached public keys.
- **No synchronous auth calls.** Public keys (JWKS) and revocations are fetched by background services and kept in memory. A request never waits on the auth service. An integration test asserts that 25 authenticated requests cause zero calls to the auth service.
- **Short TTLs + revocation.** Access tokens live 5 minutes. Revocation entries (single token by `jti`, or all tokens of a user) are published on a feed that APIs poll every 15 seconds (configurable) into a local in-memory cache. Entries are dropped once the tokens they cover would have expired, so memory stays bounded. If the feed is unreachable, APIs keep their last known state and the short TTL bounds the exposure.
- **Refresh token rotation.** Refresh tokens are single use and stored hashed. Presenting an already-used refresh token is treated as theft: the whole token family is destroyed and all access tokens of that user are revoked.
- **Gateway offloading.** The gateway authenticates the external JWT, then uses `InternalContextTokenIssuer` to mint a lightweight HMAC-signed token (default 30 s) carrying only user id, username, roles and a correlation id. Internal services accept it via the `X-Internal-Context` header. It is signed with a secret that only the gateway and internal services hold, so it cannot be forged by clients.

## Roles

A lower number is a higher privilege. Each policy means "this role **or any higher role**".

| Level | Role | Policy |
|---|---|---|
| 1 | Administrator | `RolePolicies.Administrator` |
| 2 | Lab Manager / Supervisor | `RolePolicies.LabManager` |
| 3 | Reviewer | `RolePolicies.Reviewer` |
| 4 | Lab Technician | `RolePolicies.LabTechnician` |

```csharp
app.MapGet("/results/approve", Approve).RequireAuthorization(RolePolicies.Reviewer);
```

## Using the shared library in an API

```csharp
builder.Services.AddAuthenticationCore(builder.Configuration);
// ...
app.UseAuthentication();
app.UseAuthorization();
```

```json
"AuthenticationCore": {
  "Issuer": "https://auth.local",
  "Audience": "authentication-core-api",
  "JwksUrl": "https://auth.internal/.well-known/jwks.json",
  "Revocation": { "FeedUrl": "https://auth.internal/auth/revocations", "FeedServiceKey": "<secret>" },
  "InternalContext": { "SigningKey": "<base64, at least 32 bytes>" }
}
```

For fully offline validation, set `PublicKeyPem` instead of `JwksUrl`. `InternalContext` is optional.

The library deliberately stays small: authentication, role policies and token plumbing only. Domain rules belong in each service.

## Auth service endpoints

| Method | Path | Access | Purpose |
|---|---|---|---|
| POST | `/auth/login` | anonymous | Exchange credentials for access + refresh tokens |
| POST | `/auth/refresh` | anonymous | Rotate refresh token, get a new access token |
| POST | `/auth/logout` | bearer | Revoke the current access token and refresh family |
| GET | `/auth/me` | bearer | Identity from the token |
| POST | `/auth/revoke` | Administrator | Revoke a token (`tokenId`) or all of a user's tokens (`userId`) |
| GET | `/auth/revocations?since=n` | `X-Service-Key` | Revocation feed polled by APIs |
| GET | `/.well-known/jwks.json` | anonymous | Public signing keys |

## Run it

```bash
dotnet test                                                              # 41 tests
dotnet run --project src/AuthenticationCore.Service --launch-profile http    # http://localhost:5194
dotnet run --project src/AuthenticationCore.SampleApi --launch-profile http  # http://localhost:5195
```

Development seeds one user per role (`admin`, `manager`, `reviewer`, `technician`; passwords in `src/AuthenticationCore.Service/appsettings.Development.json`).

```bash
TOKEN=$(curl -s localhost:5194/auth/login -H 'Content-Type: application/json' \
  -d '{"username":"reviewer","password":"Dev-Reviewer-Passw0rd!"}' | jq -r .accessToken)
curl -H "Authorization: Bearer $TOKEN" localhost:5195/samples/reviewer   # 200
curl -H "Authorization: Bearer $TOKEN" localhost:5195/samples/manager    # 403
```

## Container and Kubernetes

```bash
AuthenticationCore/scripts/build-image.sh        # image part11/authentication-core:local (build context: the repository root)
AuthenticationCore/scripts/test.sh --coverage    # tests
AuthenticationCore/scripts/helm-lint.sh          # lint this chart
AuthenticationCore/scripts/deploy.sh -n NS -r REL -f my-values.yaml     # install this service alone
```

* `Dockerfile`: multi-stage, chiseled non-root runtime image, listens on 8080. `GET /health` answers `200` anonymously (used by probes).
* Chart: `helm/authentication-core` (uses the shared library chart `deploy/helm/part11-common`). Whole platform: `deploy/helm/part11-platform`. Guide: [../deploy/README.md](../deploy/README.md).
* Configuration is by environment variables (`AuthService__MaxFailedAttempts`, `AuthenticationCore__Issuer`...). The chart's `values.yaml` documents every setting.
* **One replica only.** Users, refresh tokens and the revocation feed are held in memory per pod; the chart refuses `replicaCount > 1` and autoscaling unless you set `acknowledgeInMemoryStores: true`.
* **Supply the signing key** (`signingKey.existingSecret`, created by `scripts/generate-secrets.sh`), or every restart invalidates all tokens.
* **Give each seeded user a fixed `id`** (a UUID). Accounts are re-created from configuration at each start; the id is the token `sub`, which other services (for example RecordSignatures signers) store. Without a fixed id every restart would create a different identity for the same person. `SeedUser.Id` is optional for development.

## 21 CFR Part 11 notes

- Login, logout, lockout, refresh and revocation events are emitted as structured `AUDIT` log entries under the `AuthenticationCore.Audit` category.
- Accounts lock after repeated failed logins; login failures return one generic error so usernames cannot be enumerated.
- Passwords are hashed with PBKDF2-SHA256 (600,000 iterations).

## Known limitations (read before production use)

- **Stores are in memory**: users, refresh tokens and the revocation feed are lost on restart and not shared across instances. Replace `IUserStore` and the refresh/revocation stores with durable, shared implementations before running more than one instance.
- **Signing key is ephemeral** unless `AuthService:SigningKeyPemPath` points to a PEM private key. Manage it in a secret store and plan key rotation.
- **Audit trail is only a log category.** Part 11 requires secure, tamper-evident, time-stamped audit trails; route these events to a durable, access-controlled store.
- **No rate limiting or TLS termination** is built in; put both at the gateway.
- A token issued in the same second as a user-level revocation is also revoked (token timestamps have one-second resolution).
- Seed users, feed key and internal-context key in `appsettings.Development.json` are development placeholders only.
- Compliance depends on the whole system, procedures and validation, not on this code alone.

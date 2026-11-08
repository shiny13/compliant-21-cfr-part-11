# Containers and Kubernetes

How to build the three services as container images, run them with Docker Compose, and deploy them to Kubernetes with Helm.

| Service | Image | Chart | Port |
|---|---|---|---|
| AuthenticationCore | `part11/authentication-core` | `AuthenticationCore/helm/authentication-core` | 8080 |
| RecordAuditTrails | `part11/record-audit-trails` | `RecordAuditTrails/helm/record-audit-trails` | 8080 |
| RecordSignatures | `part11/record-signatures` | `RecordSignatures/helm/record-signatures` | 8080 |
| The whole platform | | `deploy/helm/part11-platform` (umbrella, with an optional PostgreSQL) | |
| Shared templates | | `deploy/helm/part11-common` (library chart) | |

> **Status of this material.** The Dockerfiles, the Compose file and the scripts were built and exercised: the images build, run read-only as a non-root user, and the full flow passes `scripts/smoke-test.sh` (26 checks) against Docker Compose. The **Helm charts were written and checked for structure, but could not be rendered, linted or installed in the environment they were written in (no `helm`/`kubectl`/`kind` available there).** Run `scripts/helm-lint.sh` and `scripts/kind-up.sh` once before relying on them, and fix anything they report. See [Validating the charts](#11-validating-the-charts).

Contents

1. [Quick start](#1-quick-start)
2. [Images](#2-images)
3. [Docker Compose](#3-docker-compose)
4. [Kubernetes with Helm](#4-kubernetes-with-helm)
5. [Secrets](#5-secrets)
6. [Configuration reference](#6-configuration-reference)
7. [Operations: upgrades, rollback, scaling, backup](#7-operations)
8. [Security posture](#8-security-posture)
9. [Part 11 considerations for deployment](#9-part-11-considerations-for-deployment)
10. [Scripts](#10-scripts)
11. [Validating the charts](#11-validating-the-charts)
12. [Troubleshooting](#12-troubleshooting)

## 1. Quick start

**Docker Compose** (needs Docker):

```bash
scripts/build-images.sh          # three images, tag "local"
docker compose up -d
scripts/smoke-test.sh            # 26 end-to-end checks
docker compose down -v
```

Services: <http://localhost:5194> (auth), <http://localhost:5196> (audit, `/swagger`), <http://localhost:5197> (signatures, `/swagger`). Development users: `admin`, `manager`, `reviewer`, `technician` (passwords in `docker-compose.yml`).

**Kubernetes on a laptop** (needs `kind`, `kubectl`, `helm`, Docker):

```bash
scripts/kind-up.sh               # cluster + images + deploy with development values
scripts/k8s-smoke-test.sh        # port-forwards and runs the same 26 checks
scripts/kind-down.sh
```

**A real cluster:**

```bash
scripts/build-images.sh --tag 1.0.0 --registry registry.example.com/team --push
scripts/generate-secrets.sh -n part11 -r part11            # prints the generated admin password once
scripts/deploy.sh -n part11 -r part11 --tag 1.0.0 --registry registry.example.com/team -f my-overrides.yaml
helm test part11 -n part11
```

## 2. Images

One Dockerfile per service (`<Project>/Dockerfile`). **The build context is the repository root**, because RecordAuditTrails and RecordSignatures reference `AuthenticationCore.Shared`:

```bash
docker build -f RecordSignatures/Dockerfile -t part11/record-signatures:local .
```

* Multi-stage: `mcr.microsoft.com/dotnet/sdk:10.0` builds, `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` runs. The runtime image has **no shell and no package manager** and runs as the non-root user `app` (uid 1654).
* Project files are copied and restored first so package downloads are cached until a `.csproj` changes.
* Listens on **8080** (`ASPNETCORE_HTTP_PORTS`). `GET /health` is the liveness/readiness probe on every service.
* Everything is configured with environment variables: ASP.NET Core maps `Section__Key` to configuration `Section:Key`, so `ConnectionStrings__AuditDb` sets `ConnectionStrings:AuditDb`.
* RecordAuditTrails and RecordSignatures understand `Database__MigrateOnStartup=true` (apply migrations at startup) and `Database__MigrateOnly=true` (with it: apply migrations, then exit with status 0 without serving). The charts use the second form in an **init container**.
* Image size is about 190 MB.
* `.dockerignore` keeps tests, docs, charts and `.git` out of the build context.

Tag images with an immutable version (`1.0.0` or the git SHA), never deploy `latest`, and record the digest in your release record (see [section 9](#9-part-11-considerations-for-deployment)).

## 3. Docker Compose

`docker-compose.yml` at the repository root runs PostgreSQL (two databases, two least-privilege roles, created by `deploy/helm/part11-platform/files/init-databases.sh`) and the three services. It is for development and evaluation: every password and key in it is a public placeholder. Users are given **fixed ids** so a person's identity (the token `sub`) survives a restart of AuthenticationCore, which keeps its accounts in memory.

The older per-project `docker-compose.yml` files (in `RecordAuditTrails/` and `RecordSignatures/`) start only the database and are meant for running a service from `dotnet run`.

## 4. Kubernetes with Helm

### What gets deployed

```
                 Ingress (optional, per chart)
                         │
   ┌─────────────────────┼──────────────────────┐
   ▼                     ▼                      ▼
authentication-core   record-audit-trails   record-signatures     Deployments, ClusterIP Services
(1 replica, see below)  (2 replicas)        (2 replicas)          non-root, read-only root FS
        │                    │ init: migrate        │ init: migrate
        │                    ▼                      ▼
        │                 ┌────────────── PostgreSQL ──────────────┐   StatefulSet (optional; use your own
        │                 │ record_audit_trails   record_signatures │   managed PostgreSQL in production)
        │                 └──────────────────────────────────────────┘
        └──── JWKS + revocation feed ───▶ the two API services; RecordSignatures also calls
              login/me/logout/revoke         AuthenticationCore and RecordAuditTrails over cluster DNS
```

Each service chart creates: `Deployment`, `Service`, `ConfigMap` (non-secret settings), `Secret` (only if you do not supply one), `ServiceAccount` (token not mounted), and optionally `Ingress`, `HorizontalPodAutoscaler`, `PodDisruptionBudget` and `NetworkPolicy`. `helm test` runs a pod that calls `/health`.

The umbrella chart `part11-platform` installs the three together, wires their URLs by release name, and (when `postgres.enabled`) adds a single-instance PostgreSQL `StatefulSet` with a `PersistentVolumeClaim`.

### Install

```bash
scripts/deploy.sh -n part11 -r part11 --tag 1.0.0 [--registry R] [-f overrides.yaml] [--dry-run]
```

The script resolves chart dependencies bottom-up (`part11-common` → each service chart → umbrella), then runs `helm upgrade --install --wait` with `values.yaml` (and `values-dev.yaml` for `--dev`). It points each service at the Secrets named `<release>-postgres`, `<release>-auth-signing-key`, `<release>-auth`, `<release>-audit`, `<release>-signatures`.

To install one service alone, use `<Project>/scripts/deploy.sh -f my-values.yaml`; then set `authentication.baseUrl`, `auditTrail.baseUrl` and `database.host` yourself.

### Per-service notes

**AuthenticationCore keeps users, refresh tokens and the revocation feed in memory.** Run **one replica** (the chart refuses more, and refuses autoscaling, unless you set `acknowledgeInMemoryStores: true` after replacing the stores with a durable shared implementation). Without `signingKey.existingSecret` it generates a new RSA key at each start, which invalidates every token and every other service's cached public key: always supply the key in a real cluster. Accounts come from `seedUsers` / the `<release>-auth` Secret and are re-created from there at each start, each with a fixed id.

**RecordAuditTrails** and **RecordSignatures** are stateless apart from PostgreSQL and scale horizontally (default 2 replicas, `PodDisruptionBudget` with `minAvailable: 1`). Migrations run in an init container before the app container; EF Core takes a database lock, so several pods starting at once is safe. Set `migrations.enabled: false` if your pipeline migrates the schema.

**RecordSignatures** will not start without `Signatures__ActiveSealKeyId` and a matching `Signatures__SealKeys__<id>` (a missing or weak key is a start-up error, by design).

### Production database

Set `postgres.enabled: false` and give each service a managed PostgreSQL (with backups, HA and TLS). Either fill `database.host/port/name/username` and supply the password through the service Secret, or put the whole connection string in the Secret (`ConnectionStrings__AuditDb`, `ConnectionStrings__SignaturesDb`). Use `SSL Mode=VerifyFull` with the CA configured. Give the application role no DDL or `TRUNCATE` rights **after** migrations have run (or run migrations with a separate, more privileged role), so the database triggers that make audit data append-only cannot be dropped by the application login.

### Ingress and TLS

`ingress.enabled: true` per service, with `className`, `hosts` and `tls`. Terminate TLS at the ingress (or a service mesh). **All traffic carrying tokens and passwords must be TLS** (also between pods if your threat model requires it). Part 11 signing sends passwords to RecordSignatures, so never expose it over plain HTTP.

### Network policy

`networkPolicy.enabled: true` (per chart) allows ingress to the pods only from pods in the same namespace plus `extraIngress` rules (for example your ingress controller's namespace). Add namespace-level default-deny policies and egress rules to match your cluster.

## 5. Secrets

`scripts/generate-secrets.sh -n NS -r RELEASE [--demo-users | --user name:Role ...]` creates, with random values and **never overwriting** an existing Secret:

| Secret | Keys | Used by |
|---|---|---|
| `<r>-postgres` | `POSTGRES_PASSWORD`, `AUDIT_DB_PASSWORD`, `SIGNATURES_DB_PASSWORD` | in-cluster PostgreSQL |
| `<r>-auth-signing-key` | `signing-key.pem` (RSA 2048) | AuthenticationCore (mounted at `/etc/authentication-core`) |
| `<r>-auth` | `AuthService__RevocationFeedServiceKey`, `AuthService__SeedUsers__N__{Id,Username,Password,Roles__0}` | AuthenticationCore |
| `<r>-audit` | `ConnectionStrings__AuditDb`, `AuthenticationCore__Revocation__FeedServiceKey` | RecordAuditTrails |
| `<r>-signatures` | `ConnectionStrings__SignaturesDb`, `Signatures__SealKeys__key-1`, `AuthenticationCore__Revocation__FeedServiceKey` | RecordSignatures |

The generated user passwords are printed once. Everything else can be read back with `kubectl get secret -o jsonpath`.

Rules:

* **Back up the seal key** (`Signatures__SealKeys__key-1`) in a secret manager, separately from database backups. Without it, existing signatures cannot be verified.
* **Rotating the seal key:** add a new key id to the Secret (`Signatures__SealKeys__key-2`), set `signatures.activeSealKeyId: key-2`, upgrade. **Keep the old key** for the whole retention period.
* **Rotating the AuthenticationCore signing key:** replace the Secret and restart; every outstanding token becomes invalid and users log in again.
* **Rotating a database password:** change it in PostgreSQL and the Secret together, then restart the pods.
* For stricter handling use an external secrets operator, sealed secrets or your cloud secret manager to create the same Secret names; the charts only reference them.
* `values-dev.yaml` contains public placeholders and creates the Secrets itself: development only.

## 6. Configuration reference

Common to all service charts (`values.yaml` of each):

| Value | Default | Meaning |
|---|---|---|
| `image.repository` / `tag` / `pullPolicy` | `part11/<service>` / chart `appVersion` / `IfNotPresent` | Image to run |
| `imagePullSecrets` | `[]` | Registry credentials |
| `replicaCount` | 1 (auth) / 2 | Replicas (ignored with autoscaling) |
| `service.type` / `service.port` | `ClusterIP` / 8080 | |
| `secret.existingSecret` | `""` | Use your own Secret instead of generating one from values |
| `config` | `{}` | Extra non-secret settings, `ENV_NAME: value` |
| `extraEnv` | `[]` | Extra container env entries (e.g. from other Secrets) |
| `resources` | small requests, memory limit | |
| `autoscaling.*` | disabled | CPU-based HPA (API services only) |
| `podDisruptionBudget.*` | enabled, `minAvailable: 1` (API services) | Applied when more than one replica |
| `ingress.*`, `networkPolicy.*`, `serviceAccount.*` | disabled / created | |
| `migrations.enabled` | `true` (API services) | Init-container migrations |
| `nodeSelector`, `affinity`, `tolerations`, `topologySpreadConstraints` | empty | Scheduling |
| `podSecurityContext`, `containerSecurityContext` | non-root 1654, read-only root FS, drop ALL, seccomp RuntimeDefault | |

Service-specific values:

| Chart | Value | Meaning |
|---|---|---|
| authentication-core | `authentication.issuer`, `audience`, `accessTokenTtl`, `refreshTokenTtl`, `maxFailedAttempts`, `lockoutDuration` | Token and lockout settings. `issuer` and `audience` must be identical in all three charts. |
| authentication-core | `signingKey.existingSecret`, `signingKey.secretKey` | RSA private key Secret |
| authentication-core | `seedUsers[]` (`id`, `username`, `password`, `roles`) | Accounts (dev only; production via `secret.existingSecret`) |
| authentication-core | `acknowledgeInMemoryStores` | Required to run more than one replica |
| record-audit-trails, record-signatures | `database.host/port/name/username/password/sslMode` | PostgreSQL. `host` defaults to `<release>-postgres` |
| record-audit-trails, record-signatures | `authentication.baseUrl`, `issuer`, `audience`, `jwksRefreshInterval`, `revocationPollInterval`, `feedServiceKey` | How tokens are validated. `baseUrl` defaults to the `authentication-core` service of the release |
| record-audit-trails | `retention.defaultYears`, `retention.byRecordType` | Retention (11.10(e)); includes the types RecordSignatures writes |
| record-signatures | `auditTrail.baseUrl` | RecordAuditTrails (defaults to the release's service) |
| record-signatures | `signatures.organizationName`, `maxFailedAttempts`, `failureWindow`, `lockoutDuration`, `reviewIntervalMonths`, `maxCommentLength`, `httpTimeout`, `alertWebhookUrl` | Signature policy (11.100, 11.300) |
| record-signatures | `signatures.activeSealKeyId`, `signatures.sealKeys` | Seal key id (required) and, for development, inline keys |
| both APIs | `swaggerEnabled` | Serve `/swagger` and `/openapi/v1.yaml` |

Umbrella chart `part11-platform` (`values.yaml`): `postgres.enabled/image/existingSecret/passwords/storage/resources`, plus one section per service chart keyed by the chart name (`authentication-core:`, `record-audit-trails:`, `record-signatures:`).

## 7. Operations

**Upgrade.** Build and push a new immutable tag, then `scripts/deploy.sh --tag NEW`. Pods roll one by one; each new pod runs the migration init container first. Changing a ConfigMap or Secret restarts the pods (checksum annotations; Secrets you reference by `existingSecret` are not watched, so restart with `kubectl rollout restart`).

**Roll back.** `helm history part11 -n part11`, then `helm rollback part11 REVISION -n part11`. EF migrations only move forward: a rollback to code older than the schema works only when the older code tolerates the newer schema. Take a database backup before any upgrade that includes a migration, and treat schema changes as expand/contract.

**Scale.** `kubectl scale` or `replicaCount` for the two API services. Keep AuthenticationCore at one replica.

**Backup and restore.** Back up both PostgreSQL databases and the seal key(s) together and test restores. Audit trails, records and signatures are the regulated data; the retention period applies to the backups too. The in-cluster PostgreSQL is a single instance with one volume: for production use a managed or replicated PostgreSQL with point-in-time recovery.

**Uninstall.** `scripts/undeploy.sh` keeps PVCs and Secrets; `--purge` removes them (irreversible).

**Monitoring.** Scrape pod logs. Watch for `SECURITY-ALERT` lines (RecordSignatures, Warning/Critical) and wire `signatures.alertWebhookUrl` to your security channel (11.300(d)). `AUDIT` lines from AuthenticationCore are its login/lockout/revocation events. Expect harmless `DataProtection ... ephemeral` warnings: the services use no cookies or anti-forgery, so the key ring is unused, and the root filesystem is read-only by design.

## 8. Security posture

| Control | How |
|---|---|
| Non-root | Pods run as uid/gid 1654; `runAsNonRoot: true` |
| Read-only root filesystem | `readOnlyRootFilesystem: true`, `/tmp` is an `emptyDir` |
| No privilege escalation, no capabilities | `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`, seccomp `RuntimeDefault` |
| Minimal image | Chiseled runtime: no shell, no package manager |
| No API token in pods | `automountServiceAccountToken: false` |
| Secrets not in values | `existingSecret` for every secret; generated by script, not committed |
| Least-privilege database roles | One role and database per service; apply migrations, then drop DDL rights if you can |
| Separation | Each service reaches the others only through their HTTP APIs |
| Optional isolation | `networkPolicy.enabled` |

Not provided here (do it in the platform): TLS certificates and termination, an admission policy (for example Pod Security Standards `restricted` on the namespace), image scanning and signing, secrets encryption at rest, resource quotas, and log retention.

## 9. Part 11 considerations for deployment

How the deployment ties to the regulation (details of each section are in [../docs/PART11-COVERAGE.md](../docs/PART11-COVERAGE.md)):

* **11.10(a) validation.** The deployed system must be qualified: IQ (the charts install the intended versions and configuration), OQ (run `scripts/smoke-test.sh` / `scripts/k8s-smoke-test.sh` and the tests), PQ (your procedures and real use). Keep the exact image **digests**, chart versions and values files used for a validated release under change control; deploy by immutable tag or digest, never `latest`.
* **11.10(c)/(e) retention and audit trails.** Persist and back up PostgreSQL for the full retention period; set `retention.byRecordType` so signature-related records outlive the records they relate to; protect the databases from privileged tampering (managed service, restricted roles, audited DBA access).
* **11.10(d)(g) access.** Do not expose the services unauthenticated; use TLS; apply network policies; keep administrator accounts few and named.
* **11.70 / 11.300 key custody.** The seal key and the AuthenticationCore signing key are the secrets that matter most: store them in a secret manager, restrict who can read the Secrets (`kubectl get secret` rights), and rotate per [../RecordSignatures/docs/SIGNATURE-PROCEDURES.md](../RecordSignatures/docs/SIGNATURE-PROCEDURES.md#9-seal-key-management).
* **11.300(d) reporting.** Connect `SECURITY-ALERT` logs and the webhook to someone who will act on them.
* **Change control.** A new image, a changed value (lockout, review interval, retention, issuer/audience), or a Kubernetes upgrade is a change to a validated system. The Helm release history plus your pipeline's records are the evidence.

## 10. Scripts

Root `scripts/`:

| Script | Purpose |
|---|---|
| `build-images.sh [--tag T] [--registry R] [--push] [service...]` | Build (and push) the images |
| `generate-secrets.sh -n NS -r REL [--demo-users] [--user name:Role]` | Create the Secrets with random values |
| `deploy.sh [-n NS] [-r REL] [--dev] [--tag T] [--registry R] [-f values] [--dry-run]` | `helm upgrade --install` the platform |
| `undeploy.sh [-n NS] [-r REL] [--purge]` | Uninstall (keeps data unless `--purge`) |
| `port-forward.sh [-n NS] [-r REL]` | localhost 5194 / 5196 / 5197 |
| `smoke-test.sh` | 26 end-to-end checks against any running platform (env vars set URLs and passwords) |
| `k8s-smoke-test.sh [-n NS] [-r REL]` | Port-forward + smoke test, passwords read from the Secret |
| `kind-up.sh` / `kind-down.sh` | Local kind cluster with images loaded and a development deploy |
| `helm-lint.sh` | Lint and render every chart (and validate with `kubeconform` if installed) |
| `test-all.sh [--coverage]` | Run every .NET test suite |
| `lib.sh` | Shared helpers |

Each project has `scripts/` too: `build-image.sh`, `test.sh [--coverage]`, `helm-lint.sh`, `deploy.sh` (that service's chart alone) and `undeploy.sh`. CI (`.github/workflows/ci.yml`) runs the tests with PostgreSQL, lints the charts, builds the images and runs the smoke test on Compose.

## 11. Validating the charts

Not yet done in the authoring environment, so do this first:

```bash
scripts/helm-lint.sh                  # helm lint + helm template, optional kubeconform
scripts/kind-up.sh                    # real install on a kind cluster
helm test part11 -n part11
scripts/k8s-smoke-test.sh
```

Things most likely to need a fix on first run: indentation inside the shared templates (`deploy/helm/part11-common/templates`), the Postgres init `ConfigMap` rendering, and value names. The Compose run proves the images, environment variable names, migration init mode and the application flow; the charts only wrap those.

## 12. Troubleshooting

| Symptom | Likely cause |
|---|---|
| Pod `Init:CrashLoopBackOff` (migrate) | PostgreSQL not reachable yet (it retries) or wrong connection string/password; `kubectl logs POD -c migrate` |
| `signatures.activeSealKeyId is required` at install | Set it, and put the same id in the Secret as `Signatures__SealKeys__<id>` |
| RecordSignatures pod fails at start: seal key | The Secret lacks `Signatures__SealKeys__<id>`, or the key is under 32 bytes / not base64 |
| `401` from the API services for every request | `issuer`/`audience` differ between charts, or the JWKS URL is unreachable (check `authentication.baseUrl`) |
| Everyone logged out after an auth pod restart | No `signingKey.existingSecret`: a new key was generated |
| Signers no longer match after an auth restart | Accounts were re-created with new ids: give every user a fixed `Id` (`generate-secrets.sh` does) |
| `authentication-core keeps users ... in memory` at install | `replicaCount` > 1 without `acknowledgeInMemoryStores` |
| Signing returns `503` | AuthenticationCore or RecordAuditTrails unreachable from RecordSignatures; check the two base URLs and network policies |

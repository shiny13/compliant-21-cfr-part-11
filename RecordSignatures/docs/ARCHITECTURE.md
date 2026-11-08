# Architecture

1. [Components](#1-components)
2. [Data model](#2-data-model)
3. [Roles and authorization](#3-roles-and-authorization)
4. [The signing sequence](#4-the-signing-sequence)
5. [Seal and hash specification](#5-seal-and-hash-specification)
6. [Verification algorithm](#6-verification-algorithm)
7. [Consistency between the three systems](#7-consistency-between-the-three-systems)
8. [Failure behavior](#8-failure-behavior)
9. [Threat model](#9-threat-model)
10. [Deployment](#10-deployment)
11. [Extending the system](#11-extending-the-system)

## 1. Components

```
                     ┌───────────────────────────┐
   user ── login ──▶ │   AuthenticationCore      │   access token (JWT, RS256, 5 min)
                     │   .Service  :5194         │◀── password check for each signing (login + /me + logout)
                     └─────────────┬─────────────┘◀── revoke a user's sessions (suspend / revoke)
                                   │ JWKS + revocation feed (background)
                                   ▼
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ RecordSignatures.Api  :5197     minimal API · validates tokens locally · OpenAPI + Swagger  │
│        │                                                                                   │
│        ▼                                                                                   │
│ RecordSignatures.Core           ALL business logic                                         │
│   SignerService  SignatureService  CredentialChallenge  SecurityAlertService  Reports      │
│   Sealing (HMAC, hashes)   Integration clients ───────────────┐                            │
│        │ store interfaces                                      │                           │
│        ▼                                                       ▼                           │
│ RecordSignatures.Database       RecordAuditTrails.Api :5196  (records + audit trail)       │
│   EF Core entities, stores,       GET/POST/PUT /api/records  ← the caller's own token      │
│   migrations, triggers                                                                      │
│        ▼                                                                                   │
│   PostgreSQL :5433  (record_signatures)                                                    │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

| Project | Responsibility | Depends on |
|---|---|---|
| `RecordSignatures.Core` | Domain types, rules, services, store/integration interfaces, HTTP clients, sealing, reporting. **No EF, no ASP.NET endpoints.** | `AuthenticationCore.Shared` (for `UserRole`), ASP.NET shared framework (for `HttpClient` factory and options) |
| `RecordSignatures.Database` | EF Core **entity models** and the PostgreSQL context, mapping to Core types, store implementations, migrations (generated schema plus hand-written trigger migration), the immutability guard. | Core |
| `RecordSignatures.Api` | Authentication/authorization wiring, endpoints, problem-detail mapping, OpenAPI, startup checks. | Core, Database, `AuthenticationCore.Shared` |

Why the entities live in the Database project and the rules in Core: Core can be unit-tested with plain in-memory fakes (no database), and the database layer can change (indexes, provider) without touching rules. Core owns the **interfaces** (`ISignerStore`, `ISignatureStore`, `ISecurityStore`, `IUnitOfWork`, `IAuditTrailClient`, `ICredentialVerifier`, `ISessionRevoker`, `ISecurityAlertSink`); the other projects implement them.

## 2. Data model

PostgreSQL database `record_signatures`. Primary keys are application-generated GUIDs. All times are UTC `timestamptz` truncated to microseconds.

### `signers` (mutable through audited lifecycle steps; never deleted)

| Column | Notes |
|---|---|
| `Id` | = id of the enrollment record in RecordAuditTrails |
| `UserId` | AuthenticationCore `sub`. **Unique forever.** Immutable. |
| `Username` | lower-cased identification code. **Unique forever.** Immutable. |
| `EmployeeId` | person identifier. **Unique among non-revoked rows** (partial index). Immutable. |
| `PrintedName`, `JobTitle` | |
| `Status`, `StatusReason`, `StatusChangedUtc` | `PendingCertification`, `Active`, `Suspended`, `Revoked` |
| `EnrolledUtc`, `EnrolledByUserId`, `EnrolledByUsername` | Immutable |
| `IdentityMethod`, `IdentityEvidence`, `IdentityVerifiedByUserId/Username`, `IdentityVerifiedUtc` | Latest identity verification (history in the audit trail) |
| `CertificationVersion`, `CertificationAcceptedUtc` | 11.100(c) acceptance |
| `LastReviewedUtc`, `NextReviewDueUtc` | 11.300(b) |
| `Version` | Optimistic concurrency token |

Triggers: `signers_identity_immutable` (rejects changes to the immutable columns), `signers_no_delete` (statement-level `DELETE`/`TRUNCATE`).

### `signatures` (append-only)

`Id` (= id of its record in RecordAuditTrails), `RecordId`, `RecordVersion`, `RecordContentHash`, `Sequence`, `SignerUserId`, `SignerUsername`, `SignerPrintedName`, `MeaningCode`, `MeaningText`, `Comment`, `SignedUtc`, `PreviousSealHash`, `SealKeyId`, `SealHash`.
Unique index `(RecordId, Sequence)`. No foreign key to `signers`, deliberately: a signature must outlive any change to a signer row. No foreign key to the record, which lives in another system.

### `signature_revocations` (append-only)

`Id`, `SignatureId` (**unique**: revoked at most once), `RevokedUtc`, `RevokedByUserId`, `RevokedByUsername`, `Reason`.

### `signing_attempts` (append-only)

`Id`, `UserId`, `Username`, `AttemptedUtc`, `Outcome` (`Success`, `BadCredentials`, `UsernameMismatch`, `SubjectMismatch`), `RecordId`. Index `(UserId, AttemptedUtc)`. Lockout is computed from this log, so there is no mutable counter to reset.

### `security_alerts` (append-only)

`Id`, `RaisedUtc`, `Severity`, `Type`, `UserId`, `Username`, `Detail`, `AuditRecordId`.

### Append-only enforcement, two layers

1. **Application:** `ImmutabilityInterceptor` (an EF `SaveChangesInterceptor`) throws on any update or delete of `signatures`, `signature_revocations`, `signing_attempts`, `security_alerts`; on any delete of a signer; and on any change to a signer's identity columns.
2. **Database:** migration `AddImmutabilityTriggers` adds row-level `BEFORE UPDATE OR DELETE` and statement-level `BEFORE TRUNCATE` triggers raising `integrity_constraint_violation`, so even a direct SQL session as the application role is refused. Row-level triggers do not fire on an empty table, so the `signers` delete/truncate trigger is statement-level.

### What is stored in RecordAuditTrails

| Record type | Created when | Updated when | Id equals |
|---|---|---|---|
| `SignerEnrollment` | a signer is enrolled | certification accepted, suspended, reinstated, revoked, reviewed | `signers.Id` |
| `ElectronicSignature` | a signature is executed | the signature is revoked (`status` changes `Executed` to `Revoked`) | `signatures.Id` |
| `SecurityAlert` | an alert is raised and the caller has a token | never | n/a (alert id is local) |

RecordAuditTrails records, for every field, the old value, new value, user, UTC time and reason, in its own hash chain. That is the audit trail for 11.10(e); this service's tables are the working data.

## 3. Roles and authorization

Token validation is the shared AuthenticationCore library: `AddAuthenticationCore` (signature via JWKS or static public key, issuer, audience, lifetime, local revocation cache). Each endpoint then requires one of the standard policies (`RolePolicies.*` meaning "this role or higher"). The acting identity is built from claims by `OperatorFactory`; the raw bearer token is kept in memory only to forward to RecordAuditTrails.

Rules enforced **in Core** (not only by endpoint policy):

* signing requires an enrollment with `Status = Active`, accepted certification, review not overdue, token username equal to the enrolled username, and `role.Satisfies(meaning.MinimumRole)`;
* an administrator cannot act on their own signer (`EnsureNotSelf`);
* reasons are mandatory (minimum 5 characters) for suspend, reinstate, revoke, review and signature revocation.

## 4. The signing sequence

`POST /api/signatures` → `SignatureService.SignAsync`:

```
 1  validate request (record id, version >= 1, meaning in catalog, comment length)
 2  load signer by token subject ........... not enrolled            → 403
 3  token username == enrolled username .... mismatch                → 403
 4  token role satisfies meaning's role .... too low                 → 403
 5  signer.EnsureCanSign(now) .............. pending/suspended/revoked/review overdue → 403
 6  GET record from RecordAuditTrails ...... missing 404 · deleted 400 · version != requested 409
 7  CredentialChallenge:
      typed code == token username? ........ no → log, Critical alert, 401
      locked out? .......................... yes → alert, 423 (no password check made)
      AuthenticationCore login + /me + logout
        rejected ........................... log, alert (Warning, or Critical on the lockout), 401
        subject != token subject ........... log, Critical alert, 401
      success → log ("signing_attempts")
 8  signedUtc = server clock (taken after the credential check)
 9  duplicate (same signer, meaning, version, not revoked)?          → 409
10  contentHash = SHA-256 over the record snapshot
11  CREATE record "ElectronicSignature" in RecordAuditTrails (caller's token)  ← its id becomes the signature id
12  sequence = last + 1, previousSeal = last.SealHash (or zeros); seal = HMAC(...)
13  INSERT into signatures (unique (RecordId, Sequence))
      on any failure → mark the audit record NotPersisted (compensation), rethrow
        (a lost race for the chain position → 409 "Another signature was applied at the same moment")
14  201 Created
```

Updates to signers and signature revocations are different, because their audit record already exists:

```
 BEGIN transaction
   UPDATE/INSERT in PostgreSQL            (flushed, not committed)
   PUT audit record in RecordAuditTrails  (if this fails → exception → ROLLBACK, nothing saved)
 COMMIT
```

## 5. Seal and hash specification

Anyone can recompute and check these independently.

**Canonical encoding** (`CanonicalHasher.Encode`): an ordered list of values, each either
`0x01 ‖ int32-little-endian byte length ‖ UTF-8 bytes` for a string, or `0x00` for null. Length-prefixing prevents ("ab","c") and ("a","bc") colliding; `0x00` makes null differ from the empty string. Hash output is lowercase hex.

**Record content hash** (`RecordContentHasher`) = `SHA-256( encode [ "RSREC1", id (D format), recordType, title, version, then for each field sorted by name (ordinal): name, value ] )`.

**Seal** (`SignatureSealer`) = `HMAC-SHA256( key[SealKeyId], encode [`
`"RSSIG1", id, recordId, recordVersion, recordContentHash, sequence, signerUserId, signerUsername, signerPrintedName, meaningCode, meaningText, comment (null allowed), signedUtc as "yyyy-MM-ddTHH:mm:ss.ffffffZ", previousSealHash, sealKeyId ] )`.

`previousSealHash` of the first signature on a record is 64 zeros. Keys must be at least 32 bytes (base64 in configuration). Comparison uses a constant-time check.

## 6. Verification algorithm

`SignatureService.VerifyRecordAsync(recordId)` (behind `GET /api/records/{id}/signatures`, the report, and `GET /api/signatures/{id}`):

1. Read the record's current snapshot from RecordAuditTrails (404 if unknown).
2. Load the record's signatures in `Sequence` order and their revocations.
3. For each signature, in order, with `expectedSequence` starting at 1 and `previousSeal` at the zeros:
   1. seal does not verify (or unknown key) → **SealInvalid**
   2. `Sequence != expectedSequence` or `PreviousSealHash != previousSeal` → **ChainBroken**
   3. same version as the record now but different content hash → **ContentMismatch**
   4. revoked → **Revoked**
   5. signed version differs from the record's current version → **RecordModifiedSinceSigning**
   6. otherwise **Valid**
   Then `expectedSequence = Sequence + 1` and `previousSeal = SealHash` (so a single missing signature is reported once).
4. `isIntact` is false if any signature is `SealInvalid`, `ChainBroken` or `ContentMismatch`; in that case a Critical `SignatureIntegrityFailure` alert is raised.

`RecordModifiedSinceSigning` and `Revoked` are normal history, not tampering, and do not make `isIntact` false.

## 7. Consistency between the three systems

Three independent stores take part: PostgreSQL (this service), RecordAuditTrails' database, and AuthenticationCore. There is no distributed transaction, so the design chooses **which inconsistency is acceptable and makes it visible**:

| Operation | Order | If a step fails | Worst case |
|---|---|---|---|
| Enroll / sign (create) | audit record first, then local insert | Local insert fails → audit record is updated to `status = NotPersisted` with a reason | If the compensation call also fails, an orphan audit record exists. It says "Executed" but has no signature. It is logged at Error; detectable by comparing `ElectronicSignature` records with `signatures`. The converse (a signature without an audit entry) cannot happen. |
| Suspend, reinstate, revoke, review, certify, revoke a signature (update) | local write in an open transaction, then audit update, then commit | Audit update fails → rollback | The audit call succeeded but the commit fails (rare): the audit trail shows a change that did not stick. Logged; the next read shows the real state. |
| Session revocation (suspend / revoke) | after the state change is saved and audited | Revocation call fails → `sessionsRevoked: false`, alert text says so | The signer is deauthorized in this service immediately (so cannot sign), but their access tokens elsewhere live until they expire (5 minutes). |
| Alerts | stored locally first, then each sink | A sink fails → logged, others still run | The alert is in `security_alerts` regardless. |

Choosing "audit first" for creates is deliberate: it guarantees that nothing is signed that the audit trail does not know about. It also makes the audit record's id usable as the entity id.

## 8. Failure behavior

| Failure | Result |
|---|---|
| AuthenticationCore unreachable during a signing | `503`; no attempt is counted; nothing signed |
| RecordAuditTrails unreachable | `503`; nothing signed, enrolled or changed |
| RecordAuditTrails rejects the token (`401/403`) | `403` "audit trail service denied access" |
| Record missing / deleted / changed | `404` / `400` / `409` |
| PostgreSQL unreachable | `500` (generic), nothing changed |
| Seal key missing or weak | API **does not start** |
| Unexpected exception | `500` with a generic message; details only in the log |

## 9. Threat model

| Threat | Defence | Residual |
|---|---|---|
| Someone signs with another person's login name | Code must equal the token's user; alert | none known |
| Stolen session token only | Password required again for every signing | Needs the password too |
| Stolen password only | Needs a valid token (login), and lockout/alerts on guesses | Attacker with both can sign: detect via alerts/audit, respond via suspension |
| Password guessing through this API | 3-failure lockout, no checks while locked; AuthenticationCore lockout too | Distributed guessing across AuthenticationCore directly is its concern |
| Insider edits a signature in the database | App guard, triggers, HMAC seal | Insider with DB owner rights **and** the seal key |
| Insider deletes a signature | Same | Deleting the newest signature(s) leaves no chain gap; cross-check with the RecordAuditTrails record |
| Replaying or copying a signature to another record | Record id, version and content hash are sealed | none known |
| Signing something different from what was reviewed | Required `recordVersion` and content hash | none known |
| Self-service signature creation or reactivation | Two-person rule; admins cannot act on themselves | Two colluding administrators |
| Reuse of a revoked identity | Unique indexes without revocation exemption; immutable identity columns | none |
| Reports used for XSS | HTML encoding of all values | none known |
| Information leakage in errors | Generic credential errors; generic 500s | none known |
| Clock manipulation | Time from the server, not the client | Server clock compromise |

## 10. Deployment

```
Kubernetes namespace
  Deployment record-signatures (2+ replicas)  ── init container: same image, Database__MigrateOnly=true
      ConfigMap  : URLs, issuer/audience, policy, active seal key id
      Secret     : connection string, Signatures__SealKeys__<id>, revocation feed key
      /health    : startup, liveness and readiness probes
  Service (ClusterIP :8080) ── optional Ingress (TLS), NetworkPolicy, HPA, PodDisruptionBudget
  calls: authentication-core:8080 (login, me, logout, revoke)   record-audit-trails:8080 (records, audit trail)
  PostgreSQL: database record_signatures, role signatures_app
```

* Stateless: any replica can serve any request; chain positions are serialized by the unique `(RecordId, Sequence)` index, not by process memory.
* Migrations run before the app container starts; concurrent pods are safe (EF Core database lock).
* Pods run non-root with a read-only root filesystem; the `Microsoft.AspNetCore.DataProtection` ephemeral-key warnings in the log are expected (no cookies are used).
* See [../../deploy/README.md](../../deploy/README.md) for the images, charts, secrets, configuration reference and operations.

## 11. Extending the system

* **Add a meaning:** extend `MeaningCatalog.All` (code, display name, statement, minimum role); add tests; revalidate. Existing signatures are unaffected because the statement is stored with each.
* **Change the certification text:** edit `CertificationStatement.Statement` and bump `CurrentVersion`; existing acceptances keep the version they accepted. Decide whether signers must re-accept.
* **Another identity provider:** implement `ICredentialVerifier` and `ISessionRevoker`; keep the "verified subject must equal the token subject" rule.
* **Another alert channel (email, pager):** implement `ISecurityAlertSink` and register it; failures are isolated by `SecurityAlertService`.
* **Different database:** the Core rules depend only on the store interfaces; port the Database project (and re-create the append-only protection natively).
* **Seal key per environment / HSM:** replace `SignatureSealer`'s key lookup; the canonical encoding stays.

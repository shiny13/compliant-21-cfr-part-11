# 21 CFR Part 11 coverage across the services

This document states, section by section, **how each project in this repository handles 21 CFR Part 11**, which parts it leaves to another project, and what stays with the organization.

The three projects are separate services that work together:

| Service | Role in the system | Detailed documentation |
|---|---|---|
| **AuthenticationCore** | Identity. Logs users in, issues short-lived signed tokens, revokes them, defines roles. Other services validate its tokens locally. | [README](../AuthenticationCore/README.md) |
| **RecordAuditTrails** | Records. Holds electronic records and their secure, time-stamped, tamper-evident audit trail, copies for inspection, retention and controlled system documentation. | [README](../RecordAuditTrails/README.md) |
| **RecordSignatures** | Signatures. Applies, seals, verifies and revokes electronic signatures on records, controls who may sign, and writes everything it does into RecordAuditTrails. | [README](../RecordSignatures/README.md), [clause-by-clause](../RecordSignatures/docs/PART11-COMPLIANCE.md) |

```
  user ─ login ─▶ AuthenticationCore ──token──▶ RecordAuditTrails  (records + audit trail)
                        ▲   ▲                          ▲
                        │   └─ password check ─┐       │ every enrollment, signature,
                        │      session revoke  │       │ revocation and alert is written here
                        └────────────────── RecordSignatures ─ signs versions of records
```

> **Read this first.** Software alone is never "Part 11 compliant". Compliance belongs to a validated system run under procedures by trained people. This document says where each service helps, where it only supports another one, where something is partial, and where the answer is a procedure. Nothing here is legal advice.

## Legend

| Mark | Meaning |
|---|---|
| **Yes** | The service implements the technical control. |
| **Part** | Implemented in part; the gap is named. |
| **Uses** | The service does not implement it itself but relies on, or feeds, the named service. |
| **Org** | A procedure, decision or paper process of the organization. Not automated. |
| **N/A** | Not applicable to this design (for example, no open system, no biometrics). |
| **No** | Not provided. Listed under gaps. |

AC = AuthenticationCore · RAT = RecordAuditTrails · RS = RecordSignatures

## 1. Coverage at a glance

| Section | Requirement (short) | AC | RAT | RS |
|---|---|---|---|---|
| **11.1 / 11.2** | Scope; electronic records may be used if Subparts B and C are met | Uses | Yes | Yes |
| **11.3** | Definitions (closed system, electronic signature, biometrics...) | N/A | N/A | N/A |
| **11.10(a)** | Validation, and ability to discern invalid or altered records | Part | Yes | Yes |
| **11.10(b)** | Accurate, complete copies, human-readable and electronic | N/A | Yes | Yes |
| **11.10(c)** | Protection of records, ready retrieval over the retention period | N/A | Yes | Yes |
| **11.10(d)** | Limit access to authorized individuals | Yes | Uses | Uses |
| **11.10(e)** | Secure, computer-generated, time-stamped audit trails | Part | Yes | Uses |
| **11.10(f)** | Operational system checks (permitted sequencing) | N/A | Yes | Yes |
| **11.10(g)** | Authority checks | Yes | Uses | Yes |
| **11.10(h)** | Device (terminal) checks | N/A | N/A | N/A |
| **11.10(i)** | Education, training, experience | Org | Org | Org |
| **11.10(j)** | Written policies holding individuals accountable | Org | Org | Org |
| **11.10(k)** | Control of system documentation | Part | Yes | Part |
| **11.30** | Open systems | N/A | N/A | N/A |
| **11.50** | Signature manifestations | N/A | N/A | Yes |
| **11.70** | Signature / record linking | N/A | Uses | Yes |
| **11.100(a)** | Unique to one individual, never reused | Yes | Uses | Yes |
| **11.100(b)** | Identity verified before sanctioning | Org | N/A | Yes |
| **11.100(c)** | Certification to FDA | N/A | N/A | Part, Org |
| **11.200(a)(1)** | Two distinct identification components | Yes | N/A | Yes |
| **11.200(a)(2)** | Used only by genuine owners | Part | N/A | Yes |
| **11.200(a)(3)** | Misuse needs two or more people | Part | N/A | Yes |
| **11.200(b)** | Biometrics | N/A | N/A | N/A |
| **11.300(a)** | Unique identification code + password | Yes | N/A | Yes |
| **11.300(b)** | Periodic check, recall, revision (password aging) | No | N/A | Part |
| **11.300(c)** | Loss management | Yes | N/A | Yes |
| **11.300(d)** | Transaction safeguards; report misuse | Part | N/A | Yes |
| **11.300(e)** | Testing of tokens and cards | N/A | N/A | N/A |

The notes below give the detail behind each row. "Part" always has an explanation.

---

## 2. Subpart A - General provisions

### 11.1 Scope and 11.2 Implementation

Part 11 lets electronic records and signatures replace paper if Subparts B and C are met, and lets organizations decide which records they keep electronically.

* **RAT** implements the records side of Subpart B: it is where a record lives and where its history is kept. **RS** implements the signature side (Subpart C and 11.50/11.70). **AC** supplies the identity both depend on.
* **Org:** decide which records are kept electronically, document that decision, and state it in the predicate-rule records. Keep the organization's predicate-rule requirements (what must be recorded and signed) separate from Part 11's requirements about *how*.

### 11.3 Definitions

None of the services needs to implement a definition, but the classification of the system matters:

* **Closed system.** All three services form a closed system: access is controlled by the people responsible for the records, through AC tokens and role checks on every endpoint.
* **Electronic signature.** RS implements electronic signatures based on an identification code plus a password (not biometrics, not digital signatures).
* **Digital signature / biometrics.** Not used. The HMAC seals and hash chains in RAT and RS provide integrity and are not PKI digital signatures.

---

## 3. Subpart B - Electronic records

### 11.10(a) Validation; discerning invalid or altered records

*Requirement: systems validated to ensure accuracy, reliability, consistent intended performance, and the ability to discern invalid or altered records.*

| | |
|---|---|
| **AC** (Part) | 41 automated tests, including the whole service and an API running together in memory, are validation evidence for the software. Records are not stored here, so "altered records" does not arise, but tokens are signed (RS256) and tampered tokens are rejected. Stores are in memory by default, so durability must be validated once replaced. |
| **RAT** (Yes) | Automated tests per layer (about 250), with line coverage near 100% including real PostgreSQL tests of migrations and triggers. **Discerning altered records:** every audit entry is hash-chained (SHA-256) per record, with `GET .../audit-trail/verify` and `GET /api/audit-trail/verify` reporting the first broken entry; document revisions are hash-chained and verifiable. |
| **RS** (Yes) | 342 automated tests (329 without PostgreSQL), 100% line coverage with PostgreSQL, and a [requirement-to-test traceability matrix](../RecordSignatures/docs/TRACEABILITY.md). **Discerning altered signatures or records:** each signature is sealed (HMAC-SHA256) and chained; verification reports `Valid`, `RecordModifiedSinceSigning`, `Revoked`, `SealInvalid`, `ChainBroken` or `ContentMismatch`, and a failed integrity check raises a Critical alert. |
| **Org** | Plan, execute and approve IQ / OQ / PQ in your environment (PostgreSQL, network, TLS, backups, identity store). Re-run the tests after any change. Automated tests are evidence for the software, not a replacement for your validation package. |

### 11.10(b) Accurate and complete copies, human-readable and electronic

| | |
|---|---|
| **AC** (N/A) | Holds no records for inspection. Its events are log lines. |
| **RAT** (Yes) | `GET /api/records/{id}/export?format=json\|csv\|text\|html` returns the record, its full audit trail and an integrity verdict. JSON and CSV are electronic; text and HTML are human-readable. CSV neutralizes spreadsheet-formula injection; HTML is encoded. An `X-Content-SHA256` header lets a copy be shown to be unaltered, and each export is itself logged. |
| **RS** (Yes) | `GET /api/records/{recordId}/signatures/report?format=json\|text\|html` returns every signature with printed name, UTC date and time, meaning, signed version, comment and verification status, with `X-Content-SHA256`. A signer's certification prints at `GET /api/signers/{userId}/certification`. |
| **Org** | Agree with the agency which formats it will accept. A record presented to an inspector should come with both the RAT export and the RS signature report: RAT exports do not embed signatures. |

### 11.10(c) Protection of records; ready retrieval throughout the retention period

| | |
|---|---|
| **AC** (N/A) | No business records. |
| **RAT** (Yes) | Records are never physically deleted (logical delete only, enforced by an EF guard and PostgreSQL triggers). Audit entries, access-log entries and document revisions are append-only. Each record gets a `RetainUntilUtc` from `Retention:ByRecordType` / `DefaultYears`; the audit trail inherits it, is never purged by the system, and remains readable for deleted records. `GET /api/retention/expired` lists disposition candidates; nothing is deleted automatically. |
| **RS** (Yes) | Signatures, revocations, attempts and alerts are append-only (guard plus triggers); signers can never be deleted. Nothing is purged by the service. Reports can be regenerated at any time. |
| **Org** | Set retention for the record types RS writes into RAT (`ElectronicSignature`, `SignerEnrollment`, `SecurityAlert`) to at least the longest signed record's retention. Back up both PostgreSQL databases **and the RS seal keys** for the whole period; test restores; plan format and media migration. |

### 11.10(d) Limiting system access to authorized individuals

| | |
|---|---|
| **AC** (Yes) | The source of access control. Login requires a password (PBKDF2-SHA256, 600,000 iterations); tokens are RS256, last 5 minutes, are refreshable by single-use rotating refresh tokens (reuse revokes the whole family), and can be revoked individually or per user. Accounts lock after 5 failed logins for 15 minutes; failures return one generic error so usernames cannot be enumerated. Inactive accounts cannot log in. Roles: Administrator, Lab Manager, Reviewer, Lab Technician. |
| **RAT** (Uses) | Every endpoint needs a valid AC token (validated locally: signature, issuer, audience, lifetime, revocation) and a minimum role. |
| **RS** (Uses) | Same, plus signing is limited to individuals who are enrolled, certified, current and allowed the chosen meaning's role. |
| **Org** | Provision and deprovision accounts promptly; restrict who administers AC; use TLS and network controls in front of all services. |

### 11.10(e) Secure, computer-generated, time-stamped audit trails

*Requirement: record the date and time of operator entries and actions that create, modify or delete records, independently; changes must not obscure previously recorded information; retain as long as the records; available for agency review and copying.*

| | |
|---|---|
| **AC** (Part) | Login, failure, lockout, refresh, reuse detection, logout and revocation events are written as structured `AUDIT` log entries. These are **not** a secure, tamper-evident, durable audit trail: they are a log category. Route them to a protected, retained store. |
| **RAT** (Yes) | **This is the audit trail of the system.** Every create, modify and delete writes one entry **per changed field**, in the same database transaction as the change, holding old value, new value, user id, username, role, a **server-generated UTC timestamp**, a reason (mandatory for modify and delete) and a SHA-256 chain hash. Entries are append-only (EF guard and PostgreSQL triggers block `UPDATE`, `DELETE`, `TRUNCATE`). Previous values are never overwritten. The operator comes only from the validated token. Retained at least as long as the record. Exportable. |
| **RS** (Uses) | Writes every action as an audited record in RAT under the **acting person's own token**: enrollment, certification, suspension, reinstatement, revocation, review, signature, signature revocation, security alert. If RAT cannot be reached, the action fails (503): nothing is signed or changed without an audit entry. Updates are transactional; creates are audit-first with compensation. See [ARCHITECTURE.md](../RecordSignatures/docs/ARCHITECTURE.md#7-consistency-between-the-three-systems). RS's own tables (signatures, attempts, alerts) are additionally append-only. |
| **Org** | Review audit trails as part of record review; protect the database from privileged tampering (least-privilege application role, DBA auditing); synchronize clocks. |

### 11.10(f) Operational system checks to enforce permitted sequencing of steps and events

| | |
|---|---|
| **AC** (N/A) | |
| **RAT** (Yes) | A deleted record can no longer be modified; an update must change something; an optimistic `expectedVersion` rejects stale writes; the chain position is unique so concurrent writers cannot fork a history; modify and delete require a reason. |
| **RS** (Yes) | Enforced order of events: enroll (administrator) → certify (individual) → sign; a signer cannot sign while pending, suspended, revoked or overdue for review; the signed version must be the record's current version; a deleted record cannot be signed; a signature can be revoked only once; a revoked or suspended signer cannot be reinstated except from `Suspended`; each chain position is taken once. |
| **Org** | Define the signing sequence required by your procedures (for example author, then review, then approval). RS enforces role per meaning but does not impose a business workflow order between meanings. |

### 11.10(g) Authority checks

| | |
|---|---|
| **AC** (Yes) | Defines roles and issues them in tokens; role policies mean "this role or any higher role". Role changes take effect at the next refresh (within the token lifetime). |
| **RAT** (Uses) | Technician: create, read, change records. Reviewer: audit trail, verification, export, retention status. Lab Manager: system-wide verification, expired-record list, access log. Administrator: create documents and revisions. |
| **RS** (Yes) | Administrator: enroll and administer signers. Lab Manager: read signers and alerts, revoke signatures. Reviewer: signature reports. Technician: sign, read. Plus meaning-specific roles (Approval needs Lab Manager) and the rule that no administrator can administer their own signature. |
| **Org** | Map roles to job functions and keep the mapping current. |

### 11.10(h) Device (terminal) checks

*Requirement: use device checks to determine the validity of the source of data input or operational instructions.*

* **All (N/A as a distinct feature).** There are no connected instruments or terminals whose identity is checked. The equivalent here is that every request carries a validated token. If instruments feed data, add device authentication at that interface.

### 11.10(i) Education, training and experience; 11.10(j) accountability policies

* **All (Org).** Procedures and training are not software. [RecordSignatures/docs/SIGNATURE-PROCEDURES.md](../RecordSignatures/docs/SIGNATURE-PROCEDURES.md) gives a training outline for signers and administrators and the policy content (never share a password; a signature is the legal equivalent of a handwritten one). The organization must issue written policies holding individuals accountable for actions under their electronic signatures (11.10(j)) and keep training records (11.10(i)).

### 11.10(k) Controls over systems documentation

*Requirement: (1) adequate controls over distribution of, access to, and use of documentation for system operation and maintenance; (2) revision and change control procedures that maintain a time-sequenced audit trail of modifications.*

| | |
|---|---|
| **AC** (Part) | The README documents operation, API and limitations; it is under version control (git history is its revision control). No in-system distribution control. |
| **RAT** (Yes) | **Built in.** `SystemDocument`s have a minimum read role; every view and every distribution (recipient and revision) is written to the append-only access log, readable by Lab Managers. Each change adds an immutable, **hash-chained** `DocumentRevision` with author, UTC time and a mandatory change description; the history is retrievable and verifiable. |
| **RS** (Part) | Its documentation ([README](../RecordSignatures/README.md), the four documents under `docs/`) is in version control, and meanings and the certification statement carry versions. Distribution tracking of these documents is not built in. Use RAT's system-documentation feature to hold controlled copies if you need access logs and revision chains. |
| **Org** | Approve, issue and withdraw SOPs; control which version is current; record distribution. |

### 11.30 Open systems

* **All (N/A).** The services are designed as a closed system. If any record or signature is accessed over an open network (internet), 11.30 requires additional measures such as encryption (TLS is required at the gateway in any case) and, where appropriate, digital signature standards. None of these services provides them by itself.

### 11.50 Signature manifestations

*Requirement: printed name, date and time, and meaning, controlled like records and shown in human-readable forms.*

| | |
|---|---|
| **AC** (N/A) | Does not sign records. Supplies the login name that becomes the identification code. |
| **RAT** (N/A) | Does not apply signatures. Its exports do not embed signatures: a displayed or printed record must be accompanied by the RS report. |
| **RS** (Yes) | Every signature stores the **printed name** (copied from the enrollment at signing time), the **UTC date and time** (server clock, microsecond precision) and the **meaning**, chosen from a closed catalog (Authorship, Responsibility, Review, Approval) with the statement copied into the signature. These are in the seal, the audit trail and every JSON / text / HTML report. They are held in append-only storage like any record. See [RS § 11.50](../RecordSignatures/docs/PART11-COMPLIANCE.md#2--1150-signature-manifestations). |

### 11.70 Signature / record linking

| | |
|---|---|
| **AC** (N/A) | |
| **RAT** (Uses) | Supplies the record's id, version and content that RS signs, and its own audit trail shows later edits to a signed record. |
| **RS** (Yes) | A signature is bound to **one version and the content hash** of one record by an HMAC seal and a per-record chain, in append-only tables with database triggers. Excising, copying, editing or moving a signature is detected; changing a signed record is shown as `RecordModifiedSinceSigning`. The signer must state the version they reviewed ("what you see is what you sign"). See [RS § 11.70](../RecordSignatures/docs/PART11-COMPLIANCE.md#3--1170-signaturerecord-linking). |

---

## 4. Subpart C - Electronic signatures

### 11.100 General requirements

| Clause | AC | RAT | RS |
|---|---|---|---|
| **(a)** unique to one individual, never reused or reassigned | **Yes.** Usernames are unique (case-insensitive) and each account has a permanent id; the in-memory store refuses duplicates. | **Uses** AC's identities for every audit entry. | **Yes.** Unique indexes on user id and identification code, forever (including after revocation); one live enrollment per person (partial unique index); identity columns immutable (guard and trigger); signers never deleted. |
| **(b)** verify identity before sanctioning | **Org.** Accounts are created by an operator or seeded (development); proof of identity is a procedure. | N/A | **Yes.** Enrollment (Administrator only) records the verification method, an evidence reference, the verifier and the time; repeated at reinstatement and every periodic review. The person cannot sign until they also certify. |
| **(c)** certify to FDA that electronic signatures are the legal equivalent of handwritten | N/A | N/A | **Part, Org.** Each individual accepts a versioned certification statement after re-entering their credentials, and a printable certification is available. The **paper certification to FDA (HFC-100)** and any later testimony are organizational acts; a checklist is in [SIGNATURE-PROCEDURES.md §3](../RecordSignatures/docs/SIGNATURE-PROCEDURES.md#3-certification-to-fda-and-by-the-individual). |

### 11.200 Electronic signature components and controls

| Clause | AC | RAT | RS |
|---|---|---|---|
| **(a)(1)** at least two distinct components (code + password) | **Yes.** Login needs username and password. | N/A | **Yes, stricter than required.** Every signing needs both components again, even within one session, and RS verifies them by logging in at AC (then ends that throw-away session). Passwords are never stored or logged by RS. |
| **(a)(2)** used only by genuine owners | **Part.** Passwords are hashed with a slow salted hash; accounts lock after 5 failures; tokens are short-lived and revocable; generic errors. It cannot detect a shared password. | N/A | **Yes.** The code typed must be the logged-in user's own; the account whose password verifies must be the logged-in account; lockout after 3 failures; every attempt logged; mismatches raise Critical alerts; suspension revokes sessions. |
| **(a)(3)** misuse requires two or more people | **Part.** Who can create accounts and reset passwords is not constrained inside AC; keep that to a small, audited group. | N/A | **Yes.** An administrator enrolls and verifies identity; only the individual can activate by certifying with their own password; no administrator can administer their own signature. Residual risk: an AC administrator who can reset passwords. |
| **(b)** biometrics | N/A | N/A | N/A |

### 11.300 Controls for identification codes and passwords

| Clause | AC | RAT | RS |
|---|---|---|---|
| **(a)** unique combination | **Yes.** Unique username; passwords salted and hashed. | N/A | **Yes.** Unique identification code per individual, never reused. |
| **(b)** periodic check, recall, revision | **No password aging or complexity rules today.** (Recall is possible: deactivate the account or revoke tokens.) | N/A | **Part.** Signing authority expires and must be re-verified by an administrator at least every `ReviewIntervalMonths` (default 12); overdue signers cannot sign; `GET /api/signers?reviewDue=true` lists them. Password revision itself is left to AC. |
| **(c)** loss management: deauthorize, replace with rigorous controls | **Yes.** `POST /auth/revoke` kills a token or all of a user's tokens; refresh-token reuse revokes the family; inactive accounts cannot log in. | N/A | **Yes.** Suspend (temporary) and revoke (permanent) deauthorize immediately and, by default, call AC to revoke the user's sessions; reinstatement needs a fresh identity verification by someone else; a permanent replacement is a new identity enrolled from scratch; compromised signatures can be revoked with a reason. |
| **(d)** transaction safeguards; detect and report attempted misuse immediately | **Part.** Failed logins, lockouts and reuse detection are logged as audit events and trigger lockout. There is no push to a security unit. | N/A | **Yes.** Every attempt is logged (append-only); lockout; alerts raised in the same request for failed authentication, identity mismatch, lockout, attempts while locked, deauthorization and integrity failure, delivered to the log, the RAT audit trail and an optional webhook. People must watch the channel. |
| **(e)** testing of tokens and cards | N/A (none) | N/A | N/A (none) |

---

## 5. Deployment: containers and Kubernetes

The services ship as container images with Helm charts ([deploy/README.md](../deploy/README.md)). How that deployment supports the regulation:

| Area | What the deployment provides | Still the organization's job |
|---|---|---|
| **11.10(a)** validation | Immutable, versioned images; Helm release history; the same 26-step smoke test (`scripts/smoke-test.sh`) for Compose and Kubernetes; `helm test`; CI that runs all suites with PostgreSQL | IQ/OQ/PQ in the target cluster; record image digests, chart versions and values for each validated release |
| **11.10(c)** protection and retention | Data lives only in PostgreSQL (services are stateless); retention values for the record types RecordSignatures writes default to 15 years; `undeploy.sh` keeps volumes and Secrets unless `--purge` | Managed or replicated PostgreSQL, backups and restore tests for the whole retention period |
| **11.10(d)(g)** access | Services are `ClusterIP` only; optional `NetworkPolicy`; optional Ingress; tokens validated locally in every pod; non-root, read-only, no capabilities, no service-account token | TLS, ingress policy, namespace Pod Security level, who may read Secrets |
| **11.10(e)** audit trail | Migrations run in an init container so the append-only triggers exist before any pod serves; the signature service writes to the audit service or fails closed | Restrict DDL/TRUNCATE for the application database role after migration; audit privileged access |
| **11.70 / 11.300** secrets | Seal key, signing key, database passwords and accounts come from Kubernetes Secrets created by `generate-secrets.sh` (never overwritten); charts reference them by name | Secret manager or sealed secrets, rotation, backup of the seal key |
| **11.300(d)** reporting | Alerts reach pod logs (`SECURITY-ALERT`) and an optional webhook | Route them to people who respond |
| **11.10(k)** documentation | Deployment guide, configuration reference and procedures are in version control | Approval and distribution under your document control |

Deployment-related items worth knowing:

* AuthenticationCore keeps users and revocations in memory, so the chart enforces **one replica** and requires a signing key Secret; accounts are re-created from configuration with **fixed ids** (a `SeedUser.Id` option added for this), otherwise a restart would give each person a new token `sub` and break RecordSignatures' signer records.
* AuthenticationCore now has `GET /health` for probes.
* RecordAuditTrails and RecordSignatures support `Database__MigrateOnly` (migrate and exit) for the init container.
* The Helm charts have not yet been rendered or installed in the environment where they were written; run `scripts/helm-lint.sh` and `scripts/kind-up.sh` as part of qualification.

## 6. Gaps and shared assumptions

| # | Gap or assumption | Owner | Effect |
|---|---|---|---|
| 1 | **No password aging or complexity rules** in AC | AC / Org | 11.300(b)'s password-revision example is procedural only. |
| 2 | **AC stores are in memory** by default (users, refresh tokens, revocation feed) and its signing key is ephemeral unless configured | AC / Org | Lost on restart, not shared across instances. Replace with durable, shared stores and a managed key before production. |
| 3 | **AC's audit events are logs**, not a tamper-evident trail | AC / Org | Ship them to a protected, retained store. |
| 4 | **Revocation reaches APIs after the feed poll interval** (15 s by default) and tokens live 5 minutes | AC | Short exposure window after a revoke. |
| 5 | **Privileged database access** (a DBA) can drop triggers | RAT, RS / Org | Hash chains and seals still reveal edits; use least-privilege roles and audit DBA access. |
| 6 | **RS seal key custody** | RS / Org | Whoever holds the key and database write access can forge signatures. Keep it in a secret store apart from the database; rotate; retain old keys. |
| 7 | **Newest signature(s) or audit entries removed without trace** cannot be spotted by the chain alone | RAT, RS | Triggers block deletes; RAT keeps an independent record of each signing to compare against. |
| 8 | **Signatures are not embedded in RAT exports** | RS / integrator | Present the RS report together with the record (11.50(b)). |
| 9 | **Printed name fixed at enrollment** | RS | Name changes need a new enrollment. |
| 10 | **Fails closed** when AC or RAT is down | RS | Intended; monitor both. |
| 11 | **TLS, rate limiting, secrets management, backups** | Deployment | Not in the applications. |
| 12 | **Time source** | Deployment | All timestamps are server UTC; keep clocks synchronized. |
| 13 | **Helm charts not yet rendered or installed** by the authors | Org | Run `scripts/helm-lint.sh` and `scripts/kind-up.sh`; qualify the deployment. |
| 14 | **Procedural requirements** (11.10(i), (j), 11.100(c), training, SOPs, validation packages) | Org | See [SIGNATURE-PROCEDURES.md](../RecordSignatures/docs/SIGNATURE-PROCEDURES.md). |

## 7. Where to look next

| If you need... | Go to |
|---|---|
| Images, Helm charts, secrets, operations, scripts | [deploy/README.md](../deploy/README.md) |
| Every signature clause with code and tests | [RecordSignatures/docs/PART11-COMPLIANCE.md](../RecordSignatures/docs/PART11-COMPLIANCE.md) and [TRACEABILITY.md](../RecordSignatures/docs/TRACEABILITY.md) |
| Operating procedures and a go-live checklist | [RecordSignatures/docs/SIGNATURE-PROCEDURES.md](../RecordSignatures/docs/SIGNATURE-PROCEDURES.md) |
| How records, audit trails, exports, retention and document control work | [RecordAuditTrails/README.md](../RecordAuditTrails/README.md) |
| Authentication, tokens, roles, revocation | [AuthenticationCore/README.md](../AuthenticationCore/README.md) |
| The three-system data flow, consistency and failure behavior | [RecordSignatures/docs/ARCHITECTURE.md](../RecordSignatures/docs/ARCHITECTURE.md) |

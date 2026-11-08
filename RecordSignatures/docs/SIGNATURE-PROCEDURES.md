# Operating procedures for electronic signatures

Part 11 controls are only as strong as the procedures around them (see 11.10, 11.100(b)/(c), 11.300). This document gives the **procedures an organization needs** to operate RecordSignatures, written as a starting point for your own SOPs. Replace the bracketed items with your own roles, forms and systems.

Roles used here are the AuthenticationCore roles: **Administrator**, **Lab Manager**, **Reviewer**, **Lab Technician**.

1. [Responsibilities](#1-responsibilities)
2. [Enrolling a signer](#2-enrolling-a-signer)
3. [Certification to FDA and by the individual](#3-certification-to-fda-and-by-the-individual)
4. [Day-to-day signing](#4-day-to-day-signing)
5. [Periodic review of signature authority](#5-periodic-review-of-signature-authority)
6. [Lost, stolen or compromised credentials](#6-lost-stolen-or-compromised-credentials)
7. [Responding to security alerts](#7-responding-to-security-alerts)
8. [Leavers, role changes and name changes](#8-leavers-role-changes-and-name-changes)
9. [Seal key management](#9-seal-key-management)
10. [Retention and availability for FDA inspection](#10-retention-and-availability-for-fda-inspection)
11. [Validation and change control](#11-validation-and-change-control)
12. [Training](#12-training)
13. [Checklist before go-live](#13-checklist-before-go-live)

---

## 1. Responsibilities

| Role | Responsible for |
|---|---|
| **Administrator** (at least two people) | Enrolling signers, verifying identity, suspending, reinstating, revoking, periodic review. One administrator can never administer their own signature, so **at least two administrators are needed.** |
| **Lab Manager** | Reading signers and security alerts, revoking signatures made in error, approving. First responder to alerts for their area. |
| **Security unit / IT security** [name it] | Receives alerts (log pipeline, webhook), investigates, escalates. Owns seal key custody together with IT. |
| **Quality assurance / regulatory** [name it] | Owns the FDA certification (11.100(c)), the SOPs, validation, inspections. |
| **Every signer** | Keeps the password secret, signs only their own signatures, reports loss or suspected compromise at once. |

## 2. Enrolling a signer

**Purpose:** establish an individual's electronic signature after verifying who they are (11.100(b)). **Who:** an Administrator who is *not* the person being enrolled.

1. The individual's manager requests a signature, stating the individual's business need and the meanings they will use (Authorship, Responsibility, Review, Approval). Their AuthenticationCore account exists and has the correct role.
2. The administrator **verifies the person's identity in person or through a controlled HR source**, using at least one of:
   * government-issued photo ID (passport, driving licence) checked against the person;
   * employee badge checked in person together with HR confirmation;
   * a verified human-resources record (for example the onboarding file) for the same person.
   Photocopies or an email from the person alone are **not** enough.
3. Obtain the person's **user id** (the `sub` shown by `GET /auth/me` on AuthenticationCore), **login name**, **legal printed name** exactly as it should print on signed records, and **employee id**.
4. Call `POST /api/signers` with those values and the identity verification: the method and an **evidence reference** that lets an inspector find what was checked (for example "Passport sighted, HR ticket HR-1043").
5. The signer is `PendingCertification` and **cannot sign yet.** Continue with [section 3](#3-certification-to-fda-and-by-the-individual).

The system records who verified, how, and when, in the enrollment and in the audit trail.

**Do not:** enroll yourself; reuse a login name or user id that belonged to someone else; enroll one person under two accounts (the system refuses while the first is live).

## 3. Certification to FDA and by the individual

### 3a. Organization to FDA (once, before electronic signatures are used) - § 11.100(c)(1)

This step is **not performed by the software.** The organization must:

1. Prepare a letter stating that the electronic signatures used in its systems, on and after the date of the letter, are intended to be the legally binding equivalent of traditional handwritten signatures.
2. Have an authorized officer sign it **by hand**.
3. Send it, on paper, to the **FDA Office of Regional Operations (HFC-100)** [confirm the current address with Regulatory Affairs], and keep a copy and proof of delivery.
4. On FDA request, provide **additional certification or testimony** that a specific electronic signature is the legally binding equivalent (11.100(c)(2)). For a specific person, print their certification with `GET /api/signers/{userId}/certification` and attach the signature report for the record in question (`GET /api/records/{recordId}/signatures/report?format=text`).

### 3b. Individual certification (every signer)

1. The signer logs in at AuthenticationCore and reads the statement (`CertificationStatement`, version 1.0):
   > I certify that my electronic signature, applied within the systems of [organization], is intended to be the legally binding equivalent of my traditional handwritten signature. I will not share my identification code or password with anyone, and I will report any loss or suspected compromise immediately.
2. They call `POST /api/signers/me/certification` with their login name, **their password** and the statement version.
3. The signer becomes `Active`. File the printed certification (`GET /api/signers/{userId}/certification`) in the personnel / quality file.

## 4. Day-to-day signing

* The signer reviews the record, noting its **version** (shown by RecordAuditTrails).
* They sign through the application, choosing the meaning that matches the procedure step, typing their **login name and password for every signature.** The software never "remembers" a signing.
* If the record changed after review, signing is refused (`409`): review the new version and sign again.
* A signer must **never** let anyone else type in or see their password, sign "on behalf of" someone, or leave a signed-in session unattended.
* An error in a signature is not edited: a **Lab Manager revokes the signature with a reason** (`POST /api/signatures/{id}/revoke`), and the correct signature is then applied.
* Wherever a signed record is displayed or printed, **show the signature block** (printed name, date/time, meaning): use `GET /api/records/{recordId}/signatures/report`.

## 5. Periodic review of signature authority

**Purpose:** periodically check and, where needed, recall signature authority (11.300(b)). **Frequency:** at most every `Signatures:ReviewIntervalMonths` (default 12).

1. Monthly, a Lab Manager or Administrator lists signers due: `GET /api/signers?reviewDue=true`.
2. For each, the administrator confirms the person still works here, still needs a signature at their role, and **re-verifies their identity** ([section 2](#2-enrolling-a-signer), step 2).
3. If all is well, call `POST /api/signers/{userId}/review` with a reason and the new identity verification. The next review date moves out.
4. If not, revoke ([section 8](#8-leavers-role-changes-and-name-changes)).

A signer whose review is **overdue cannot sign** until reviewed; this is intentional. Passwords themselves must also be changed periodically under the identity provider's policy [define it: AuthenticationCore does not enforce password aging today].

## 6. Lost, stolen or compromised credentials

**Purpose:** deauthorize at once and replace with rigorous controls (11.300(c)). **Trigger examples:** password written down or shared, phishing, lost unlocked device, a security alert naming the person, or the person reports a concern.

**Immediately (any administrator, within minutes):**

1. `POST /api/signers/{userId}/suspend` with the reason. By default this also revokes the person's AuthenticationCore sessions; check `sessionsRevoked` in the answer. If it is `false`, revoke the sessions manually in AuthenticationCore (`POST /auth/revoke`).
2. Reset the person's password in AuthenticationCore through the controlled process (not by the person alone).
3. Notify the Lab Manager and the security unit.

**Investigate (Lab Manager with QA):**

4. List what the person signed since the suspected time: `GET /api/records/{recordId}/signatures` for the affected records, and the person's audit trail records in RecordAuditTrails.
5. Review security alerts (`GET /api/security-alerts`) around the same period.
6. For any signature that cannot be trusted, **revoke it with a reason** (`POST /api/signatures/{id}/revoke`) and arrange for a new signature once control is re-established.

**Replace:**

7. **Temporary (credentials recovered or reset):** an administrator **other than the signer** re-verifies identity and calls `POST /api/signers/{userId}/reinstate`. The review period restarts.
8. **Permanent (compromise confirmed, or account to be retired):** `POST /api/signers/{userId}/revoke`. If the person still needs to sign, create a **new account**, then enroll it from scratch ([section 2](#2-enrolling-a-signer)); the old identity is never reused.

Record the incident in your deviation / CAPA system with links to the audit trail entries.

## 7. Responding to security alerts

Alerts are produced immediately (11.300(d)) to three places: the **application log** (`SECURITY-ALERT`, Warning or Critical), the **RecordAuditTrails** audit trail (record type `SecurityAlert`), and the optional **webhook** (`Signatures:AlertWebhookUrl`). Connect at least one of these to a channel that someone watches.

| Alert | Severity | What it means | Response |
|---|---|---|---|
| `FailedSignatureAuthentication` | Warning | A password check failed. | Usually a typo. Several in a row from one person: contact them. |
| `SignerLockedOut` | **Critical** | The failure limit was hit; signing is locked for 15 minutes. | Contact the person. Confirm they were the one trying. If not, treat as [section 6](#6-lost-stolen-or-compromised-credentials). |
| `AttemptWhileLocked` | Warning | Someone tried to sign during a lockout. | Same as above; repeated attempts suggest an attack or a user who needs help. |
| `IdentityMismatch` | **Critical** | A signing used another person's login name or account. | **Treat as attempted unauthorized use.** Contact both people; suspend the person using the other's code until explained. |
| `SignerDeauthorized` | Warning | An administrator suspended or revoked a signer. | Confirm it matches an open incident or change request. |
| `SignatureIntegrityFailure` | **Critical** | A signature failed its seal, chain or content check. | Treat as possible tampering: preserve the database, notify QA and security, compare against RecordAuditTrails, and investigate before any further use of the record. |

Management is informed through the incident process. Keep a record of each Critical alert and how it was closed.

## 8. Leavers, role changes and name changes

* **Leaver:** revoke on the last working day (`POST /api/signers/{userId}/revoke`, reason "Left the company", sessions revoked). Their past signatures remain valid.
* **Role change:** AuthenticationCore roles decide which meanings a person may use. Update the role there. Run a review ([section 5](#5-periodic-review-of-signature-authority)) if the signing needs change substantially.
* **Name change (e.g. legal name):** the printed name on past signatures must not change. Revoke the old enrollment, create the new account, enroll with the new legal name and fresh identity verification. Because the old enrollment is revoked, the same employee id can be used again.
* **Account renamed in AuthenticationCore:** signing is refused while the token's login name differs from the enrolled one. Treat it as a name change.

## 9. Seal key management

The seal key makes signatures unforgeable by anyone who only has database access.

* **Generate** at least 32 random bytes: `openssl rand -base64 48`. Store in a secret manager (not in the repository, not with the database password). Inject through configuration `Signatures:SealKeys:<id>` and `Signatures:ActiveSealKeyId`.
* **Access:** only the service identity and a very small number of named custodians.
* **Rotation** (at least yearly, and immediately if exposure is suspected):
  1. Add a new key with a new id (for example `2027-01`) **next to the old one.**
  2. Set `ActiveSealKeyId` to the new id and deploy. New signatures use it.
  3. **Keep the old key** in `SealKeys` for as long as signatures sealed with it must verify, i.e. for the full retention period. Removing a key makes its signatures show `SealInvalid`.
* **Backup:** back up keys separately from database backups, with the same retention.
* **If a key is exposed:** rotate at once, then re-verify all signed records (`GET /api/records/{id}/signatures`), and treat the window as a security incident.
* The API refuses to start without a valid active key.

## 10. Retention and availability for FDA inspection

* **Retention.** Signatures, enrollments, attempts and alerts are **never deleted** by the service. In RecordAuditTrails, set `Retention:ByRecordType` for `ElectronicSignature`, `SignerEnrollment` and `SecurityAlert` to at least the longest retention of any record type that is signed. Back up both PostgreSQL databases and the seal keys for the same period.
* **Ready retrieval.** For any signed record, an inspector can be given within minutes:
  * the signature report in human-readable and electronic form: `GET /api/records/{id}/signatures/report?format=text|html|json` (with its SHA-256 digest);
  * each signature's audit trail: `GET {audit}/api/records/{signatureId}/audit-trail`, and the record's own audit trail from RecordAuditTrails;
  * each signer's certification: `GET /api/signers/{userId}/certification`, and the enrollment's audit trail (identity verification, certification, any suspension or review);
  * integrity evidence: `isIntact: true` from the verification, and RecordAuditTrails' audit verification.
* **Inspector's access:** give a read-only account with the **Reviewer** or **Lab Manager** role; never share administrator credentials.

## 11. Validation and change control

* The automated tests in this repository ([TRACEABILITY.md](TRACEABILITY.md)) are evidence for the *software* behavior. The organization still performs installation, operational and performance qualification in its own environment (PostgreSQL, AuthenticationCore, RecordAuditTrails, network, TLS, backups).
* Re-run the tests (including the PostgreSQL variants: `SIGNATURES_TEST_PG`) when any of these change: the code, .NET or package versions, PostgreSQL major version, the identity provider.
* Changes to the meaning catalog, the certification statement (bump `CertificationStatement.CurrentVersion`), lockout settings or review interval go through change control.
* Keep the configuration (apart from secrets) in version control.

## 12. Training

Before enrollment, each signer is trained and records that they understand:

* their signature is the legal equivalent of their handwritten signature;
* never share, write down or store the password; never sign for someone else;
* every signing needs the password again, by design;
* what to do if they think the password is compromised (tell an administrator **immediately**);
* what each meaning (Authorship, Responsibility, Review, Approval) commits them to.

Administrators and managers are additionally trained on sections 2, 5, 6 and 7.

## 13. Checklist before go-live

- [ ] FDA paper certification sent and filed (§ 3a)
- [ ] At least two Administrators exist; none administers their own signature
- [ ] Seal key generated, stored in the secret manager, custodians named; `ActiveSealKeyId` set
- [ ] RecordAuditTrails retention set for `ElectronicSignature`, `SignerEnrollment`, `SecurityAlert`
- [ ] Alerts wired to a monitored channel (webhook or log pipeline); response owners named
- [ ] AuthenticationCore: durable user store, configured signing key, password policy and reset process agreed
- [ ] PostgreSQL: least-privilege application role (no DDL, no TRUNCATE), backups tested, restore tested
- [ ] TLS in front of all three services; servers on NTP
- [ ] Systems that display signed records show the signature report
- [ ] Training records complete; SOPs approved
- [ ] IQ/OQ/PQ executed, including the PostgreSQL test variants

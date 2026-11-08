# How RecordSignatures addresses 21 CFR Part 11

This document walks through the electronic-signature requirements of 21 CFR Part 11, one clause at a time. For each clause it states **the requirement**, **how the system meets it**, **where in the code**, **the evidence** (automated tests, see also [TRACEABILITY.md](TRACEABILITY.md)), and **what the organization must still do**.

> **Scope and honesty.** Software cannot be "Part 11 compliant" on its own. Compliance is a property of a validated system operated under procedures by trained people. This project supplies the technical controls and says plainly where a requirement is procedural, partly met, or not applicable. Nothing here is legal advice.

Sections covered: **11.50** signature manifestations, **11.70** signature/record linking, **11.100** general requirements, **11.200** electronic signature components and controls, **11.300** controls for identification codes and passwords.

Contents

1. [Terms and the building blocks](#1-terms-and-the-building-blocks)
2. [§ 11.50 Signature manifestations](#2--1150-signature-manifestations)
3. [§ 11.70 Signature/record linking](#3--1170-signaturerecord-linking)
4. [§ 11.100 General requirements](#4--11100-general-requirements)
5. [§ 11.200 Electronic signature components and controls](#5--11200-electronic-signature-components-and-controls)
6. [§ 11.300 Controls for identification codes/passwords](#6--11300-controls-for-identification-codespasswords)
7. [Related Part 11 requirements this service relies on](#7-related-part-11-requirements-this-service-relies-on)
8. [Gaps, assumptions and residual risks](#8-gaps-assumptions-and-residual-risks)
9. [Summary matrix](#9-summary-matrix)

---

## 1. Terms and the building blocks

| Part 11 term (§ 11.3) | In this system |
|---|---|
| **Electronic signature** | The act of an enrolled individual submitting a signing request that carries their **identification code** (login name) and **password**, for a chosen **meaning**, on one **version** of a **record**. The result is a stored, sealed `SignatureRecord`. |
| **Handwritten signature** | Not used. (Part 11 also covers handwritten signatures executed to electronic records; none are handled here.) |
| **Closed system** | Access is controlled by the people responsible for the records: every call needs an AuthenticationCore token, and every role is checked. |
| **Digital signature** | Not used. The seal (HMAC) proves the integrity and linking of a signature; it is not the signature itself and is not a PKI digital signature. |
| **Biometrics** | Not used. |

Building blocks, and the project each lives in:

| Building block | Purpose | Code |
|---|---|---|
| **Signer** (enrollment) | An individual sanctioned to sign: identity, printed name, identity verification, certification, review dates, status. | `Core/Models/Signer.cs`, `Core/Services/SignerService.cs`, entity `Database/Entities/Entities.cs` (`SignerEntity`), table `signers` |
| **Meaning catalog** | The closed list of meanings and the role each needs. | `Core/Models/SignatureMeaning.cs` (`MeaningCatalog`) |
| **Credential challenge** | The two-component check before every signing, with lockout and alerts. | `Core/Services/CredentialChallenge.cs` |
| **Signature** | The executed, sealed signature. | `Core/Services/SignatureService.cs`, table `signatures` |
| **Sealer** | HMAC-SHA256 over everything that gives a signature meaning. | `Core/Sealing/SignatureSealer.cs`, `RecordContentHasher.cs`, `CanonicalHasher.cs` |
| **Revocation** | Withdrawing a signature, kept as a separate row. | `signature_revocations` |
| **Attempt log** | Every credential check, success or failure. | `signing_attempts` |
| **Security alerts** | Reports of attempted unauthorized use. | `Core/Services/SecurityAlertService.cs`, `AlertSinks.cs`, `security_alerts` |
| **Audit trail** | Every enrollment, certification, signing, revocation and alert is written to RecordAuditTrails. | `Core/Integration/AuditTrailClient.cs`, `Core/Services/AuditFields.cs` |
| **Identity check** | Password verification by the AuthenticationCore service. | `Core/Integration/AuthenticationCoreClient.cs` |

Two design rules run through everything below:

* **Fail closed.** If AuthenticationCore or RecordAuditTrails cannot be reached, the operation fails with `503` and nothing is signed or changed. A signature without an audit trail entry cannot exist.
* **The actor is the token, never the request.** The user id, username and role come only from the validated AuthenticationCore token (`Api/Security/OperatorFactory.cs`). Request bodies never say who is acting.

---

## 2. § 11.50 Signature manifestations

> **(a)** Signed electronic records shall contain information associated with the signing that clearly indicates all of the following: **(1)** The printed name of the signer; **(2)** The date and time when the signature was executed; and **(3)** The meaning (such as review, approval, responsibility, or authorship) associated with the signature.
>
> **(b)** The items identified in paragraphs (a)(1), (a)(2), and (a)(3) of this section shall be subject to the same controls as for electronic records and shall be included as part of any human readable form of the electronic record (such as electronic display or printout).

### (a)(1) Printed name of the signer

* **How.** At enrollment an administrator enters the signer's `printedName` (for example "Terry Technician"). Every signature **copies** that name into its own row (`SignerPrintedName`) at the moment of signing and includes it in the seal. The name shown is therefore the name at signing time, even if the enrollment changes later. The login name (`SignerUsername`, the identification code) is stored too.
* **Code.** `SignerService.EnrollAsync`; `SignatureService.SignAsync` (builds the `SignatureRecord`); `SignatureRecord.SignerPrintedName`.
* **Evidence.** `SignatureServiceTests.Sign_ManifestsPrintedNameDateTimeAndMeaning`; `SigningFlowTests.ARecordIsSignedWithNameDateTimeAndMeaning_AndTheSigningIsAudited`; seal test `SealingTests.Sealer_DetectsEveryFieldBeingChanged` proves editing the name breaks the seal.
* **Organization.** Enter the legal name as it should print. A name change is handled by revoking and re-enrolling (see [Gaps](#8-gaps-assumptions-and-residual-risks)).

### (a)(2) Date and time of execution

* **How.** The time is taken from the **server clock** (`TimeProvider`, UTC) at the moment the credentials have been verified, truncated to microseconds so that it is identical after a PostgreSQL round trip. The client cannot supply or alter it. It is stored with the signature, included in the seal, repeated in the audit trail record, and shown as ISO 8601 UTC (for example `2026-11-08T12:21:19.052565Z`) in every report. RecordAuditTrails adds its own independent server timestamp to the audit entry.
* **Code.** `SignatureService.SignAsync` (`signedUtc`), `Core/Sealing/TimestampPrecision.cs`.
* **Evidence.** `SignatureServiceTests.Sign_ManifestsPrintedNameDateTimeAndMeaning` (exact timestamp from a controlled clock); `PostgresIntegrationTests.SignatureSeals_SurviveThePostgresMicrosecondRoundTrip`.
* **Organization.** Synchronize servers with a trusted time source (NTP). If local time must be displayed, the front end converts from UTC.

### (a)(3) Meaning

* **How.** The signer picks one meaning from a **closed catalog**; free text cannot stand in for it.

  | Code | Statement stored with the signature | Minimum role |
  |---|---|---|
  | `Authorship` | I am the author of this record and attest that its content is accurate and complete. | LabTechnician |
  | `Responsibility` | I accept responsibility for the content of this record and the work it describes. | LabTechnician |
  | `Review` | I have reviewed this record and found it accurate and complete. | Reviewer |
  | `Approval` | I approve this record for its intended use. | LabManager |

  The statement is **copied into each signature**, so the meaning that was shown at signing stays readable even if wording is later changed. An optional comment (maximum 500 characters) is stored too. `GET /api/meanings` lists the catalog.
* **Code.** `Core/Models/SignatureMeaning.cs`; `SignatureService.SignAsync` (meaning and role checks).
* **Evidence.** `ModelTests.MeaningCatalog_*`; `SignatureServiceTests.Sign_MeaningNeedsTheRightRole`; `SigningFlowTests.Meanings_AreListed`.
* **Organization.** Decide which meaning each signature step in a procedure uses. To add a meaning, extend `MeaningCatalog` and re-validate.

### (b) Same controls as electronic records; shown in human-readable forms

* **Same controls as records.** Name, time and meaning are stored in an **append-only** table (`signatures`): the application guard (`ImmutabilityInterceptor`) and PostgreSQL triggers reject any `UPDATE`, `DELETE` or `TRUNCATE`. They are sealed, so a change is detected. They are retained with no automatic deletion. Every create is written to the audit trail with the old value (none), new value, user, time and reason; a revocation is a *new* row plus an audit modification, never an edit.
* **Human-readable and electronic forms.** `GET /api/records/{recordId}/signatures/report?format=text|html|json` produces a report where each signature shows **printed name, date/time and meaning**, the record version it covers, any comment, and its verification status. Text and HTML are for people (HTML values are encoded to prevent script injection), JSON is for systems. The response carries `X-Content-SHA256` so a copy can be shown to be unaltered.
* **Code.** `Core/Reporting/SignatureReport.cs`, `Core/Services/SignatureReportService.cs`; `Database/ImmutabilityInterceptor.cs`; `Database/Migrations/*AddImmutabilityTriggers.cs`.
* **Evidence.** `SignatureServiceTests.Report_ShowsNameDateTimeAndMeaning_InEveryFormat`; `ReportingTests.Html_EncodesEveryUserSuppliedValue`; `SigningFlowTests.Reports_AreAvailableInHumanReadableAndElectronicForm_WithADigest`; `PostgresIntegrationTests.Triggers_BlockUpdateDeleteAndTruncate_OnEveryAppendOnlyTable`.
* **Organization.** Any screen or printout of a signed record **must include the signature report** (or equivalent data from `GET .../signatures`). RecordAuditTrails' own exports do not embed signatures. See [Gaps](#8-gaps-assumptions-and-residual-risks).

---

## 3. § 11.70 Signature/record linking

> Electronic signatures and handwritten signatures executed to electronic records shall be linked to their respective electronic records to ensure that the signatures cannot be excised, copied, or otherwise transferred to falsify an electronic record by ordinary means.

### How a signature is tied to one record

A signature binds to **one exact version of one record**:

1. **The signer says which version they reviewed.** `recordVersion` is mandatory. If the record has changed since, the request is rejected with `409` ("Review the current version before signing"). *What you see is what you sign.*
2. **A content hash of what the signer saw.** The service reads the record from RecordAuditTrails and hashes its id, type, title, version and all fields (sorted, length-prefixed so field boundaries cannot be confused) with SHA-256 (`RecordContentHasher`). That hash is stored as `RecordContentHash`.
3. **A seal.** `SignatureSealer` computes an **HMAC-SHA256** over: the signature id, record id, record version, content hash, chain position, signer id and username, printed name, meaning code and statement, comment, timestamp, the previous signature's seal, and the key id. The HMAC key is a server secret.
4. **A per-record chain.** Each signature stores the previous signature's seal (`PreviousSealHash`, the first uses 64 zeros) and a gap-free `Sequence`. A unique index on `(RecordId, Sequence)` stops two simultaneous signers from forking the chain.

### What that prevents ("ordinary means")

| Attack | Why it fails |
|---|---|
| **Excise** a signature (delete its row) | `DELETE` and `TRUNCATE` are blocked by the application guard and by database triggers. If it were done anyway, the next signature's `Sequence`/`PreviousSealHash` no longer match: status `ChainBroken`. |
| **Edit** a signature (change name, meaning, time, comment...) | `UPDATE` is blocked. If done anyway, the HMAC no longer matches: `SealInvalid`. |
| **Copy** a signature onto another record | The record id, version and content hash are inside the seal: `SealInvalid`. |
| **Transfer** a signature to a different version of the same record | Version and hash are inside the seal. A *newer* version is shown as `RecordModifiedSinceSigning`; the signature only ever claims the version it was made on. |
| **Alter the record** but keep the signatures looking valid | If the version moved on, the signature says `RecordModifiedSinceSigning` (the change itself is in the RecordAuditTrails audit trail). If content changed *without* the version moving (direct database tampering in RecordAuditTrails), the hash differs: `ContentMismatch`. |
| **Forge** a new signature with SQL | The row can be inserted, but without the **secret seal key** the HMAC cannot be computed. (Someone holding both the key and database write access can forge: see [Gaps](#8-gaps-assumptions-and-residual-risks).) |
| **Reorder or insert** signatures | Chain fields and the seal break. |

`GET /api/records/{recordId}/signatures` runs this verification live on every call and returns, per signature, one of: `Valid`, `RecordModifiedSinceSigning`, `Revoked`, `SealInvalid`, `ChainBroken`, `ContentMismatch`, and an overall `isIntact`. A failed integrity check also raises a **Critical** security alert.

### Revocation is separate

A manager can withdraw a signature (`POST /api/signatures/{id}/revoke`, reason mandatory). That inserts a `signature_revocations` row and updates the signature's RecordAuditTrails record (status `Executed` to `Revoked`, with the reason and who). The signature row itself is never touched, so its seal still verifies and the history remains.

### Key rotation

Seals name their key (`SealKeyId`). `Signatures:SealKeys` may hold several keys; `ActiveSealKeyId` signs new signatures and **old keys remain so old signatures still verify**. See the procedure in [SIGNATURE-PROCEDURES.md](SIGNATURE-PROCEDURES.md#9-seal-key-management).

* **Code.** `Core/Sealing/*`; `SignatureService.SignAsync` and `VerifyRecordAsync`/`Classify`; `Database/SignaturesDbContext.cs` (unique index); `Database/Migrations/*AddImmutabilityTriggers.cs`.
* **Evidence.** `SealingTests.*` (every field breaks the seal; key rotation); `SignatureServiceTests.Verify_*` (modified record, content mismatch, edited signature, copied signature, removed signature, missing middle signature, unknown key); `SignatureServiceTests.Sign_WhatYouSeeIsWhatYouSign_StaleVersionIsRejected`; `SigningFlowTests.TamperingWithTheDatabase_IsDetected_AndRaisesACriticalAlert`; `PostgresIntegrationTests.TwoSignersRacingForTheSameChainPosition_OnlyOneWins`; `CrossServiceTests.EditingTheRealRecordAfterSigning_ShowsOnTheSignature`.

---

## 4. § 11.100 General requirements

### (a) Each electronic signature shall be unique to one individual and shall not be reused by, or reassigned to, anyone else

* **How.**
  * An individual's signature **is** their enrollment (`signers` row), keyed by their AuthenticationCore user id (`UserId`) and their identification code (`Username`). **Both have unique database indexes** and are unique **forever, including after revocation**.
  * **One live enrollment per person.** The organization's person identifier (`EmployeeId`) has a *partial* unique index that excludes revoked enrollments. The same person cannot hold two live signatures, but after a revocation (for example, a compromised account) they may be enrolled again under a new account.
  * **Never reassigned.** The identity columns (`UserId`, `Username`, `EmployeeId`, `EnrolledUtc`, enrolled-by) cannot change: the application guard rejects it and a PostgreSQL trigger rejects it even for direct SQL. Signer rows cannot be deleted (a statement-level trigger blocks `DELETE` and `TRUNCATE` even when no row matches). Attempting to enroll a revoked user id again is refused with "an electronic signature is never reassigned or reused".
  * **Never shared.** Signing needs the individual's own password (see 11.200); the typed identification code must equal the logged-in user's.
* **Code.** `SignerService.EnrollAsync`; `SignaturesDbContext.OnModelCreating` (indexes); `ImmutabilityInterceptor`; migration trigger `prevent_signer_identity_change`.
* **Evidence.** `SignerServiceTests.Enroll_SameUserTwice_IsAConflict`, `...SameIdentificationCodeForAnotherUser...`, `...SamePersonTwice_IsAConflict_UntilTheFirstEnrollmentIsRevoked`, `...ARevokedIdentityIsNeverReassigned`; `ImmutabilityTests.SignerIdentityColumns_CanNeverChange`, `Signers_CanNeverBeDeleted`; `PostgresIntegrationTests.Triggers_NeverAllowASignersIdentityToBeReassigned`, `Triggers_NeverAllowASignerToBeDeleted_EvenWhenNoRowMatches`, `EmployeeId_CanBeReusedOnlyAfterRevocation`, `UniqueViolations_BecomeConflicts`; `SignerLifecycleTests.Identities_AreNeverSharedOrReassigned`.
* **Organization.** Do not recycle AuthenticationCore user ids or usernames. Use a stable `EmployeeId`.

### (b) Before an organization establishes, assigns, certifies, or otherwise sanctions an individual's electronic signature, the organization shall verify the identity of the individual

* **How.** Enrollment is an **Administrator-only** act and cannot be completed without identity verification: the administrator picks the **method** (`GovernmentIssuedId`, `EmployeeBadgeInPerson`, `HumanResourcesRecord`, `Other`) and writes an **evidence reference** (for example "Passport sighted by HR, ticket HR-1043"). The verifier (from the token) and the time (server clock) are recorded with it, and printed on the certification. Identity verification is **repeated** on reinstatement and on every periodic review, each time overwriting the current verification and writing the old one to the audit trail. The signer stays in `PendingCertification` and **cannot sign** until the individual also accepts the certification (c).
* **Code.** `SignerService.EnrollAsync`, `ReinstateAsync`, `ReviewAsync`, `BuildIdentity`; `IdentityVerification` record.
* **Evidence.** `SignerServiceTests.Enroll_CreatesAPendingSigner_WithVerifiedIdentity_AndAnAuditRecord`, `Enroll_RejectsInvalidInput...` (identity missing / invalid method / blank evidence), `Reinstate_RequiresFreshIdentityVerification...`; `SignerLifecycleTests.Enrollment_RecordsHowTheIdentityWasVerified_AndWhoDidIt`, `EnrollmentValidation_ReturnsProblemDetails`.
* **Organization.** Define what counts as adequate evidence ([SIGNATURE-PROCEDURES.md](SIGNATURE-PROCEDURES.md#2-enrolling-a-signer)). The system records *that* and *how* it was done; it cannot verify the document itself.

### (c) Persons using electronic signatures shall certify to the agency that the electronic signatures in their system are intended to be the legally binding equivalent of traditional handwritten signatures

> (1) submitted in paper form with a traditional handwritten signature to the FDA Office of Regional Operations (HFC-100); (2) additional certification or testimony on request that a specific electronic signature is the legally binding equivalent.

* **How, in the system (individual level).** After enrollment the individual must accept a **certification statement** (`CertificationStatement.Statement`, version `1.0`): that their electronic signature is the legally binding equivalent of their handwritten signature, that they will not share their code or password, and that they will report loss or compromise. To accept, they re-enter both signature components. The acceptance time and statement version are stored, audited, and printed:
  `GET /api/signers/{userId}/certification` returns a printable "Certification of Electronic Signature" with the individual's name, identification code, identity verification, the statement, and when it was accepted. This is the supporting record for the agency submission.
* **How, in the organization (agency level).** The **paper certification to FDA (11.100(c)(1)) is the organization's act and is not automated**: it is a letter, signed by hand, submitted to FDA before or at the time electronic signatures are first used. Likewise, additional certification or testimony on request (11.100(c)(2)) is a procedure. [SIGNATURE-PROCEDURES.md](SIGNATURE-PROCEDURES.md#3-certification-to-fda-and-by-the-individual) gives a checklist and what to attach from this system.
* **Code.** `Core/Models/CertificationStatement.cs`; `SignerService.AcceptCertificationAsync`, `GetCertificationAsync`.
* **Evidence.** `SignerServiceTests.Certification_*`; `ModelTests.CertificationRender_ShowsAcceptedAndPendingStates`; `SignerLifecycleTests.TheCertificationIsPrintable`; `SignatureServiceTests.Sign_RequiresCertificationToHaveBeenAccepted`.

---

## 5. § 11.200 Electronic signature components and controls

### (a) Electronic signatures that are not based upon biometrics shall:

#### (a)(1) Employ at least two distinct identification components such as an identification code and password

* **How.** The two components are the **identification code** (the login name) and the **password**. The signing request must carry both (`username`, `password`) and the service verifies them **with AuthenticationCore** by performing a login with them (`AuthenticationCoreClient.VerifyAsync`), reading `/auth/me` to learn the account's id, then immediately logging that throw-away session out. Passwords are never stored or logged by this service; they are used for that one call. Because the check is a real login, **AuthenticationCore's own failed-attempt lockout also counts these attempts**.
  * **(i) A series of signings in one continuous controlled session**: the standard allows later signings to use only one component. This system is **stricter**: it requires **both components for every signing**, so the "first signing" rule is always met.
  * **(ii) Signings not performed in a single continuous session**: each signing uses all components. Always true here.
* **Code.** `Core/Services/CredentialChallenge.cs`; `Core/Integration/AuthenticationCoreClient.cs`; `SignatureService.SignAsync`.
* **Evidence.** `SignatureServiceTests.Sign_RequiresThePasswordEveryTime`; `SigningFlowTests.EverySigningNeedsThePasswordAgain`; `CredentialChallengeTests.BlankComponents_AreRejectedWithoutBeingCounted`; `AuthenticationCoreClientTests.Verify_LogsIn_ReadsTheSubject_AndEndsTheThrowawaySession`.
* **Organization.** Users must be told not to let a browser or tool store the password for signing.

#### (a)(2) Be used only by their genuine owners

Technical controls that make use by anyone else hard, detected and reported:

| Control | Detail |
|---|---|
| **Only your own code** | The identification code typed must equal the logged-in user's (from the token). Otherwise: refused, logged as `UsernameMismatch`, **Critical alert** (`IdentityMismatch`). The identity provider is not even asked. |
| **Only your own account** | The account whose password verified must be the logged-in account (`sub` compared); otherwise `SubjectMismatch`, Critical alert. |
| **Session and password** | An attacker needs both a valid session token *and* the password. |
| **Lockout** | 3 failures within 15 minutes lock signing for 15 minutes (configurable). The correct password does not unlock early. |
| **Revocation** | Suspending or revoking a signer also revokes their AuthenticationCore sessions. |
| **Generic errors** | Failures never say whether the code or the password was wrong. |

* **Code.** `CredentialChallenge.VerifyAsync`; `SignerService` (`FinishDeauthorizationAsync`).
* **Evidence.** `CredentialChallengeTests.AnotherPersonsIdentificationCode_IsACriticalAlert_AndNeverReachesTheIdentityProvider`, `CredentialsOfADifferentAccount_AreACriticalAlert`, `ThreeFailures_LockSigning_EvenForTheCorrectPassword`, `LockExpires_AfterTheLockoutDuration`; `SigningFlowTests.SigningAsSomeoneElse_IsRefusedAndAlerted`, `RepeatedFailures_Lock_With423_AndAreReportedToTheSecurityUnit`.

#### (a)(3) Be administered and executed to ensure that attempted use of an individual's electronic signature by anyone other than its genuine owner requires collaboration of two or more individuals

Use of someone else's signature must need **more than one person to cooperate**:

* **Creating** a signature takes **two people**: an **Administrator** enrolls and verifies the identity, and the **individual themself** then activates it by accepting the certification with their own password. Neither can do it alone.
* **An administrator cannot enroll, suspend, reinstate, revoke or review themself**; the service refuses with a pointer to 11.200(a)(3). A second administrator must act.
* The administrator never learns or sets the individual's password in this service; it lives only in AuthenticationCore.
* Attempting to sign as someone else needs that person's **password and an active session** (token).

> **Residual risk.** An AuthenticationCore administrator who can reset passwords could, with a second step (logging in as that person), sign as them. That is a control of the identity provider, outside this service: restrict and audit password resets there.

* **Evidence.** `SignerServiceTests.Enroll_Self_IsForbidden_BecauseSigningNeedsTwoPeople`, `Suspend_RequiresAReason_NotYourself_AndAnExistingSigner`, `Review_RequiresAnActiveSigner_AndSomeoneElse`; `SignerLifecycleTests.AdministratorsCannotEnrollThemselves`, `AdministratorsCannotAdministerThemselves_OrUnknownSigners`.

### (b) Electronic signatures based upon biometrics shall be designed to ensure that they cannot be used by anyone other than their genuine owners

* **Not applicable.** No biometric signatures are used. If biometrics are added later, this requirement needs its own design and validation.

---

## 6. § 11.300 Controls for identification codes/passwords

> Persons who use electronic signatures based upon use of identification codes in combination with passwords shall employ controls to ensure their security and integrity.

### (a) Maintaining the uniqueness of each combined identification code and password, such that no two individuals have the same combination

* **How.** The *combination* is unique because the **identification code is unique** to one individual (unique index on `signers.Username` and on `signers.UserId`, never reused even after revocation), so no two individuals can share a code, whatever their passwords. Passwords are held and hashed (PBKDF2-SHA256, 600,000 iterations) by AuthenticationCore, where login names are also unique.
* **Evidence.** `SignerStoreTests.UserId_AndUsername_AreUniqueForever`; `PostgresIntegrationTests.UniqueViolations_BecomeConflicts`.
* **Organization.** Keep the AuthenticationCore user store free of duplicate or recycled names.

### (b) Ensuring that identification code and password issuances are periodically checked, recalled, or revised (for example, to cover such events as password aging)

* **How (this service): periodic re-verification of signing authority.** Each signer has `NextReviewDueUtc` (default 12 months after enrollment, reinstatement or review). When it passes, **signing is blocked** ("the periodic review is overdue") until an administrator completes `POST /api/signers/{userId}/review`, which **re-verifies the individual's identity** (method and evidence again) and sets the next date. `GET /api/signers?reviewDue=true` lists who is due. Each review is audited with its reason.
* **How (recall):** any signer can be **recalled** at any time by suspension or revocation (see (c)).
* **Not provided here: password aging.** Forcing passwords to be *revised* is a function of the identity provider. AuthenticationCore does not currently expire passwords. Until it does, this part of the example in (b) is covered only procedurally. See [Gaps](#8-gaps-assumptions-and-residual-risks).
* **Code.** `Signer.IsReviewOverdue`, `Signer.EnsureCanSign`, `SignerService.ReviewAsync`, `ISignerStore.ListAsync(reviewDueOnly)`.
* **Evidence.** `ModelTests.OverdueReview_BlocksSigningUntilReviewed`; `SignatureServiceTests.Sign_IsBlockedWhenThePeriodicReviewIsOverdue_AndResumesAfterReview`; `SigningFlowTests.Sign_IsBlockedWhenReviewIsOverdue`; `SignerStoreTests.List_FiltersOrdersAndPages`.

### (c) Following loss management procedures to electronically deauthorize lost, stolen, missing, or otherwise potentially compromised tokens, cards, and other devices that bear or generate identification code or password information, and to issue temporary or permanent replacements using suitable, rigorous controls

* **How.** Credentials here are knowledge-based (code and password); there are no tokens or cards, so "loss" means a lost, shared, written-down or suspected-compromised password or session.
  * **Suspend** (`POST /api/signers/{userId}/suspend`, reason required): deauthorizes immediately and *temporarily*. By default it also **revokes the user's AuthenticationCore sessions**, so existing tokens stop working once APIs receive the revocation (within the feed poll interval, 15 s by default). A security alert is raised.
  * **Revoke** (`.../revoke`): *permanent* deauthorization. The identity is never reused.
  * **Temporary replacement / reinstatement** (`.../reinstate`): only for a suspended signer, and only with a **fresh identity verification** by an administrator other than the signer. The review period restarts. The individual still holds the same code; the password is reset in AuthenticationCore as part of the procedure.
  * **Permanent replacement:** revoke, then enroll a **new identity** (new account) with full identity verification and certification. The one-live-enrollment-per-person rule allows this precisely because the old enrollment is revoked.
  * Existing signatures made before the loss stay on record; a manager may **revoke individual signatures** (with a reason) that were made with a compromised credential.
  * If the session revocation call fails, the result says `sessionsRevoked: false` and the alert states it so the administrator can revoke manually.
* **Code.** `SignerService.SuspendAsync`, `ReinstateAsync`, `RevokeAsync`, `FinishDeauthorizationAsync`; `Signer.Suspend/Reinstate/Revoke`; `AuthenticationCoreClient.RevokeUserSessionsAsync`; `SignatureService.RevokeAsync`.
* **Evidence.** `SignerServiceTests.Suspend_*`, `Reinstate_*`, `Revoke_*`; `SignatureServiceTests.Sign_IsBlockedWhileSuspended_AndAfterRevocation`; `SignerLifecycleTests.LossManagement_SuspendRevokesSessions_BlocksSigning_AndAlerts`, `Reinstatement_NeedsAFreshIdentityCheck_AndRestoresSigning`, `Revocation_IsPermanent`; `AuthenticationCoreClientTests.RevokeSessions_*`.

### (d) Use of transaction safeguards to prevent unauthorized use of passwords and/or identification codes, and to detect and report in an immediate and urgent manner any attempts at their unauthorized use to the system security unit, as appropriate, to organizational management

* **Prevent.** Every credential check (success and failure) is written to the append-only `signing_attempts` table. Repeated failures **lock** signing (3 within 15 minutes, 15 minute lock). While locked, no password check is made at all (`423 Locked`), so a locked account cannot be brute-forced and cannot be kept locked indefinitely by attempts.
* **Detect and report immediately.** These events each raise a **security alert** synchronously, in the same request:

  | Alert type | Severity | When |
  |---|---|---|
  | `FailedSignatureAuthentication` | Warning | A password check failed (shows "n of 3") |
  | `SignerLockedOut` | Critical | The failure limit was reached |
  | `AttemptWhileLocked` | Warning | A signing was tried during a lockout |
  | `IdentityMismatch` | Critical | Another person's code or account was presented |
  | `SignerDeauthorized` | Warning | An administrator suspended or revoked a signer |
  | `SignatureIntegrityFailure` | Critical | A seal, chain or content check failed |

  Each alert is **stored first** (so it cannot be lost) and then pushed to every sink, none of which can block the others or the request:
  1. **Log** at Warning / Critical level (`LoggingAlertSink`, tagged `SECURITY-ALERT`, for log-based monitoring and paging);
  2. **RecordAuditTrails** (`AuditTrailAlertSink`): a `SecurityAlert` record in the audit trail;
  3. **Webhook** (`WebhookAlertSink`, optional `Signatures:AlertWebhookUrl`): JSON posted at once to the security operations channel.
  Managers read alerts with `GET /api/security-alerts` (`minimumSeverity`, `from`).
* **To management.** Wire the webhook or log pipeline to the security unit and to quality / management (see the response steps in [SIGNATURE-PROCEDURES.md](SIGNATURE-PROCEDURES.md#7-responding-to-security-alerts)). The software provides the signal; people must receive and act on it.
* **Code.** `CredentialChallenge`; `SecurityAlertService`; `AlertSinks.cs`; tables `signing_attempts`, `security_alerts`.
* **Evidence.** `CredentialChallengeTests.*`; `AlertTests.Raise_StoresFirst_ThenNotifiesEverySink`, `FailingSink_NeverBlocksTheOthersOrTheCaller`, `LoggingSink_UsesALevelMatchingTheSeverity`, `WebhookSink_PostsTheAlertAsJson`; `CrossServiceTests.SecurityAlerts_AreRecordedInTheRealAuditTrail`; `SigningFlowTests.RepeatedFailures_Lock_With423_AndAreReportedToTheSecurityUnit`.

### (e) Initial and periodic testing of devices, such as tokens or cards, that bear or generate identification code or password information to ensure that they function properly and have not been altered in an unauthorized manner

* **Not applicable.** No tokens, cards or other devices are used. If hardware authenticators are added, device testing procedures and controls would be needed.

---

## 7. Related Part 11 requirements this service relies on

| Requirement | How it is met |
|---|---|
| **11.10(d)** limit system access to authorized individuals; **11.10(g)** authority checks | Every endpoint needs an AuthenticationCore token validated locally (signature, issuer, audience, lifetime, revocation) and an enforced role policy. Signing needs an active, certified, current enrollment and the role of the meaning. |
| **11.10(e)** secure, computer-generated, time-stamped audit trails | Each enrollment, certification, suspension, reinstatement, revocation, review, signature, signature revocation and security alert is written to **RecordAuditTrails** as an audited record, with old/new value per field, the acting user (their own token), server time and reason. Signatures are append-only; changes are new rows. |
| **11.10(c)** protection of records to enable accurate and ready retrieval | Signatures are never purged by the service; reports can be produced at any time. |
| **11.10(a)** validation | See [TRACEABILITY.md](TRACEABILITY.md) for the automated evidence; the organization still owns IQ/OQ/PQ. |
| **11.10(k)** documentation controls | This repository's documents are under version control; see procedures. |

Every audited entity uses **the same id as its record in RecordAuditTrails** (a signer's id, a signature's id), so the full audit trail of any signature or enrollment is one call away: `GET {audit}/api/records/{id}/audit-trail`.

---

## 8. Gaps, assumptions and residual risks

| # | Item | Effect | Mitigation |
|---|---|---|---|
| 1 | **No password aging** in AuthenticationCore | 11.300(b)'s password-revision example is procedural only | Add password expiry to AuthenticationCore, or enforce by procedure. Periodic re-verification of authority is built in. |
| 2 | **Seal key custody** | Anyone with the key and DB write access can forge signatures | Keep the key in a secret store apart from DB credentials; restrict access; rotate; use a least-privilege DB role. |
| 3 | **DB owner can drop triggers** | Removes the database-level append-only protection | Application role without DDL/TRUNCATE; audit DBA access; the seal still detects edits. |
| 4 | **Newest signatures removed with no trace** | Chain cannot show what is missing at the end | Triggers block deletes; compare against the RecordAuditTrails records (`ElectronicSignature`) which hold an independent entry for every signing. |
| 5 | **Audit entry is created before the signature row** | If the database save fails after the audit call, an audit record exists without a signature | The service marks that audit record `NotPersisted` with a reason (compensation). If even that fails, the orphan is logged. Updates are transactional: the database write is rolled back if the audit call fails. |
| 6 | **Printed name fixed at enrollment** | A legal name change needs re-enrollment | Revoke and enroll a new identity; old signatures keep the old name, correctly. |
| 7 | **Signature report separate from RecordAuditTrails exports** | A printout of a record may omit signatures | Procedure and integration: always include the signature report where the record is displayed or printed (11.50(b)). |
| 8 | **AuthenticationCore limits** (in-memory stores, ephemeral signing key by default, revocation delay) | Affects identity, sessions and revocation timing | See its README; configure durable stores and keys before production. |
| 9 | **11.100(c) FDA certification** | Paper process | Organizational procedure (checklist provided). |
| 10 | **Time source** | Timestamps only as good as the server clock | NTP, monitored. |
| 11 | **Two services must both be up to sign** | Fails closed (503) when either is down | Intentional; monitor both. |
| 12 | **Hardware/biometric controls absent** | 11.200(b), 11.300(e) N/A | Revisit if introduced. |
| 13 | **TLS, rate limiting, secrets management** | Not in the application | Deployment/gateway layer. |

---

## 9. Summary matrix

| Clause | Status | Main controls |
|---|---|---|
| 11.50(a)(1) printed name | Met | Stored per signature, in the seal, in reports and audit trail |
| 11.50(a)(2) date/time | Met | Server UTC clock, microsecond precision, in the seal |
| 11.50(a)(3) meaning | Met | Closed catalog with stored statement and role per meaning |
| 11.50(b) same controls; in human-readable form | Met (integration needed) | Append-only + seal + audit; text/HTML/JSON report. Displays must include the report. |
| 11.70 linking | Met ("by ordinary means") | Version + content hash + HMAC seal + chain + triggers |
| 11.100(a) unique, never reused | Met | Unique indexes, one live enrollment per person, immutable identity, triggers |
| 11.100(b) identity verified | Met (procedure defines adequacy) | Method + evidence + verifier recorded; repeated at review and reinstatement |
| 11.100(c) certification | Individual part met; FDA submission is organizational | Accepted statement with re-authentication; printable certification |
| 11.200(a)(1) two components | Met (stricter than required) | Code + password every signing, verified by AuthenticationCore |
| 11.200(a)(2) genuine owner only | Met (technical controls) | Own code only, own account only, lockout, alerts, session revocation |
| 11.200(a)(3) collaboration of two or more | Met | Admin enrolls, individual activates; admins cannot administer themselves |
| 11.200(b) biometrics | N/A | Not used |
| 11.300(a) unique code+password | Met | Unique identification code |
| 11.300(b) periodic check/revise | Partly met | Periodic re-verification of authority; password aging is not provided |
| 11.300(c) loss management | Met | Suspend, revoke, reinstate with re-verification, session revocation |
| 11.300(d) transaction safeguards, reporting | Met (people must receive alerts) | Attempt log, lockout, immediate alerts to log, audit trail and webhook |
| 11.300(e) device testing | N/A | No devices |

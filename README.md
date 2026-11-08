# compliant-21-cfr-part-11

This repository is a collection of projects designed to comply with **21 CFR Part 11**, the U.S. Food and Drug Administration (FDA) regulation governing electronic records and electronic signatures.

## Background

Title 21 of the Code of Federal Regulations, Part 11 (21 CFR Part 11) defines the criteria under which the FDA considers electronic records and electronic signatures to be trustworthy, reliable, and equivalent to paper records and handwritten signatures. It applies to organizations in FDA-regulated industries such as pharmaceuticals, biotechnology, medical devices, and clinical research.

## Scope

Each project in this directory is built with Part 11 requirements in mind. Core areas of focus include:

- **Audit trails** – secure, computer-generated, time-stamped records of operator actions that create, modify, or delete electronic records.
- **Electronic signatures** – unique to one individual, linked to their respective records, and including the signer's printed name, date/time, and meaning of the signature.
- **Access controls** – limiting system access to authorized individuals, with authority checks and unique user identification.
- **Record integrity and retention** – protecting records so they remain accurate, readable, and retrievable throughout the retention period.
- **System validation** – documented evidence that systems consistently perform as intended.
- **Operational and documentation controls** – controls over system documentation, operation, and maintenance.

## Repository Structure

The repository is a small system of three cooperating services, each in its own subdirectory with its own documentation describing the Part 11 requirements it addresses.

```
  user ─ login ─▶ AuthenticationCore ──token──▶ RecordAuditTrails  (records + audit trail)
                        ▲   ▲                          ▲
                        │   └─ password check ─┐       │ every enrollment, signature,
                        │      session revoke  │       │ revocation and alert is written here
                        └────────────────── RecordSignatures ─ signs versions of records
```

| Project | Description |
|---|---|
| [AuthenticationCore](AuthenticationCore/README.md) | Centralized JWT authentication service with a shared, stateless validation library, token revocation and role-based access. |
| [RecordAuditTrails](RecordAuditTrails/README.md) | Electronic records with a hash-chained, append-only audit trail (old/new value, who, when, why), FDA-ready exports, retention and revision-controlled system documentation on PostgreSQL. |
| [RecordSignatures](RecordSignatures/README.md) | Electronic signatures (§§ 11.50, 11.70, 11.100, 11.200, 11.300): printed name, date/time and meaning on every signature, sealed to one version of a record, two-component re-authentication through AuthenticationCore, identity verification and certification, lockout and security alerts, and every action recorded in RecordAuditTrails. |

## How each service handles 21 CFR Part 11

**[docs/PART11-COVERAGE.md](docs/PART11-COVERAGE.md)** goes through the regulation section by section (11.1 to 11.300) and states what each service does, what it relies on another service for, what is partial, and what stays with the organization. In summary:

| Part 11 area | AuthenticationCore | RecordAuditTrails | RecordSignatures |
|---|---|---|---|
| **11.10(a)** validation, detecting altered records | Automated tests | Tests; hash-chain verification of audit entries and revisions | Tests with requirement traceability; seal and chain verification of signatures |
| **11.10(b)** copies for inspection | n/a | JSON, CSV, text and HTML exports with SHA-256 digest | JSON, text and HTML signature reports with digest; printable certification |
| **11.10(c)** protection and retention | n/a | Logical delete only, append-only history, retention dates, expiry listing | Append-only signatures, nothing purged |
| **11.10(d)(g)** access limits, authority checks | Login, short-lived revocable tokens, lockout, roles | Role policy on every endpoint | Role policy, per-meaning roles, only enrolled certified signers sign |
| **11.10(e)** audit trail | Events logged (not tamper-evident) | **The audit trail**: field-level old/new value, who, when, why, hash-chained | Writes every action into RecordAuditTrails; fails closed without it |
| **11.10(f)** sequencing checks | n/a | Version checks, no edits after delete, reasons required | Enroll, certify, then sign; signed version must be current |
| **11.10(k)** system documentation | Versioned README | Role-restricted, access-logged, revision-controlled documents | Versioned documents |
| **11.50** signature manifestations | n/a | n/a | Printed name, UTC date/time, meaning on every signature and report |
| **11.70** signature linking | n/a | Supplies the record | HMAC seal to one record version and content; chained; append-only |
| **11.100** general requirements | Unique accounts | Uses its identities | Unique, never reassigned; identity verified; certification |
| **11.200** components and controls | Password login | n/a | Code and password for every signing; own code only; two-person enrollment |
| **11.300** code and password controls | Hashing, lockout, revocation | n/a | Lockout, immediate alerts, suspend and revoke, periodic re-verification |

Not covered by any service: password aging and complexity rules, the paper certification to FDA (11.100(c)), training and written policies (11.10(i)(j)), validation packages, and open-system controls (11.30). The coverage document lists these as gaps, together with the operating procedures in [RecordSignatures/docs/SIGNATURE-PROCEDURES.md](RecordSignatures/docs/SIGNATURE-PROCEDURES.md).

## Disclaimer

The projects in this repository are provided as examples and reference implementations. Compliance with 21 CFR Part 11 depends on the complete system, procedures, and organizational controls in which software is used. Nothing here constitutes legal or regulatory advice; consult qualified regulatory and quality professionals and perform your own validation before using any software in a regulated environment.

## License

See [LICENSE](LICENSE) for details.

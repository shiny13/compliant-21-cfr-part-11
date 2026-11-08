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

Projects will be added as individual subdirectories, each with its own documentation describing the Part 11 requirements it addresses.

| Project | Description |
|---|---|
| [AuthenticationCore](AuthenticationCore/README.md) | Centralized JWT authentication service with a shared, stateless validation library, token revocation and role-based access. |
| [RecordAuditTrails](RecordAuditTrails/README.md) | Electronic records with a hash-chained, append-only audit trail (old/new value, who, when, why), FDA-ready exports, retention and revision-controlled system documentation on PostgreSQL. |
| [RecordSignatures](RecordSignatures/README.md) | Electronic signatures (§§ 11.50, 11.70, 11.100, 11.200, 11.300): printed name, date/time and meaning on every signature, sealed to one version of a record, two-component re-authentication through AuthenticationCore, identity verification and certification, lockout and security alerts, and every action recorded in RecordAuditTrails. |

## Disclaimer

The projects in this repository are provided as examples and reference implementations. Compliance with 21 CFR Part 11 depends on the complete system, procedures, and organizational controls in which software is used. Nothing here constitutes legal or regulatory advice; consult qualified regulatory and quality professionals and perform your own validation before using any software in a regulated environment.

## License

See [LICENSE](LICENSE) for details.

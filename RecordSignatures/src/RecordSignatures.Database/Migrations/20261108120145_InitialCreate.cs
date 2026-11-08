using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecordSignatures.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RaisedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AuditRecordId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signature_revocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RevokedByUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signature_revocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordVersion = table.Column<int>(type: "integer", nullable: false),
                    RecordContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    SignerUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SignerUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SignerPrintedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MeaningCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    MeaningText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SignedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousSealHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SealKeyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SealHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrintedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EmployeeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    StatusChangedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EnrolledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EnrolledByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EnrolledByUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentityMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IdentityEvidence = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IdentityVerifiedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentityVerifiedByUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IdentityVerifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CertificationVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CertificationAcceptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastReviewedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextReviewDueUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signing_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AttemptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signing_attempts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_alerts_RaisedUtc",
                table: "security_alerts",
                column: "RaisedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_signature_revocations_SignatureId",
                table: "signature_revocations",
                column: "SignatureId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signatures_RecordId_Sequence",
                table: "signatures",
                columns: new[] { "RecordId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signatures_SignerUserId",
                table: "signatures",
                column: "SignerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_signers_EmployeeId",
                table: "signers",
                column: "EmployeeId",
                unique: true,
                filter: "\"Status\" <> 'Revoked'");

            migrationBuilder.CreateIndex(
                name: "IX_signers_NextReviewDueUtc",
                table: "signers",
                column: "NextReviewDueUtc");

            migrationBuilder.CreateIndex(
                name: "IX_signers_UserId",
                table: "signers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signers_Username",
                table: "signers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signing_attempts_UserId_AttemptedUtc",
                table: "signing_attempts",
                columns: new[] { "UserId", "AttemptedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_alerts");

            migrationBuilder.DropTable(
                name: "signature_revocations");

            migrationBuilder.DropTable(
                name: "signatures");

            migrationBuilder.DropTable(
                name: "signers");

            migrationBuilder.DropTable(
                name: "signing_attempts");
        }
    }
}

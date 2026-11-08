using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecordSignatures.Database.Migrations
{
    /// <summary>
    /// Database-level enforcement that survives any application bug or direct SQL through the application role:
    /// signatures, revocations, signing attempts and security alerts are insert-only; signers can never be deleted and
    /// their identity columns can never change (21 CFR 11.100(a), 11.70).
    /// </summary>
    public partial class AddImmutabilityTriggers : Migration
    {
        private static readonly string[] AppendOnlyTables = ["signatures", "signature_revocations", "signing_attempts", "security_alerts"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION prevent_modification() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% on % is not permitted: the table is append-only (21 CFR Part 11 electronic signatures)', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;");

            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($@"
CREATE TRIGGER {table}_append_only BEFORE UPDATE OR DELETE ON {table}
    FOR EACH ROW EXECUTE FUNCTION prevent_modification();
CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table}
    FOR EACH STATEMENT EXECUTE FUNCTION prevent_modification();");
            }

            migrationBuilder.Sql(@"
CREATE FUNCTION prevent_signer_identity_change() RETURNS trigger AS $$
BEGIN
    IF NEW.""Id"" IS DISTINCT FROM OLD.""Id""
        OR NEW.""UserId"" IS DISTINCT FROM OLD.""UserId""
        OR NEW.""Username"" IS DISTINCT FROM OLD.""Username""
        OR NEW.""EmployeeId"" IS DISTINCT FROM OLD.""EmployeeId""
        OR NEW.""EnrolledUtc"" IS DISTINCT FROM OLD.""EnrolledUtc""
        OR NEW.""EnrolledByUserId"" IS DISTINCT FROM OLD.""EnrolledByUserId""
        OR NEW.""EnrolledByUsername"" IS DISTINCT FROM OLD.""EnrolledByUsername"" THEN
        RAISE EXCEPTION 'The identity of an electronic signer cannot be changed or reassigned (21 CFR 11.100(a))'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER signers_identity_immutable BEFORE UPDATE ON signers
    FOR EACH ROW EXECUTE FUNCTION prevent_signer_identity_change();
CREATE TRIGGER signers_no_delete BEFORE DELETE OR TRUNCATE ON signers
    FOR EACH STATEMENT EXECUTE FUNCTION prevent_modification();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER signers_no_delete ON signers; DROP TRIGGER signers_identity_immutable ON signers; DROP FUNCTION prevent_signer_identity_change();");
            foreach (var table in AppendOnlyTables)
            {
                migrationBuilder.Sql($"DROP TRIGGER {table}_append_only ON {table}; DROP TRIGGER {table}_no_truncate ON {table};");
            }
            migrationBuilder.Sql("DROP FUNCTION prevent_modification();");
        }
    }
}

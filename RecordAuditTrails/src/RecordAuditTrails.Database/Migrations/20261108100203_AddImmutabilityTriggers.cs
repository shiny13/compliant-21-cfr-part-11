using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecordAuditTrails.Database.Migrations
{
    /// <summary>
    /// Database-level enforcement of 11.10(e): audit entries, access log entries and document revisions are
    /// append-only, and records / documents can never be physically deleted, even by someone with direct SQL access
    /// through the application role. (A database owner can still drop triggers; protect that with DB privileges.)
    /// </summary>
    public partial class AddImmutabilityTriggers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE FUNCTION prevent_modification() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION '% on % is not permitted: the table is append-only (21 CFR Part 11 audit trail)', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;");

            foreach (var table in new[] { "audit_trail", "access_log", "document_revisions" })
            {
                migrationBuilder.Sql($@"
CREATE TRIGGER {table}_append_only BEFORE UPDATE OR DELETE ON {table}
    FOR EACH ROW EXECUTE FUNCTION prevent_modification();
CREATE TRIGGER {table}_no_truncate BEFORE TRUNCATE ON {table}
    FOR EACH STATEMENT EXECUTE FUNCTION prevent_modification();");
            }

            foreach (var table in new[] { "records", "system_documents" })
            {
                migrationBuilder.Sql($@"
CREATE TRIGGER {table}_no_delete BEFORE DELETE OR TRUNCATE ON {table}
    FOR EACH STATEMENT EXECUTE FUNCTION prevent_modification();");
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "audit_trail", "access_log", "document_revisions" })
            {
                migrationBuilder.Sql($"DROP TRIGGER {table}_append_only ON {table}; DROP TRIGGER {table}_no_truncate ON {table};");
            }
            foreach (var table in new[] { "records", "system_documents" })
            {
                migrationBuilder.Sql($"DROP TRIGGER {table}_no_delete ON {table};");
            }
            migrationBuilder.Sql("DROP FUNCTION prevent_modification();");
        }
    }
}

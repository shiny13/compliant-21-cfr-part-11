using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database;

public class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<ElectronicRecord> Records => Set<ElectronicRecord>();

    public DbSet<AuditTrailEntry> AuditTrail => Set<AuditTrailEntry>();

    public DbSet<AccessLogEntry> AccessLog => Set<AccessLogEntry>();

    public DbSet<SystemDocument> Documents => Set<SystemDocument>();

    public DbSet<DocumentRevision> DocumentRevisions => Set<DocumentRevision>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC; Npgsql refuses non-UTC DateTimes for timestamptz.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ElectronicRecord>(e =>
        {
            e.ToTable("records");
            e.HasKey(x => x.Id);
            e.Property(x => x.RecordType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.DataJson).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.ModifiedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.RecordType);
            e.HasIndex(x => x.RetainUntilUtc);
        });

        modelBuilder.Entity<AuditTrailEntry>(e =>
        {
            e.ToTable("audit_trail");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.FieldName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50).IsRequired();
            e.Property(x => x.PreviousHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Hash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            // The unique chain position stops two concurrent writers from forking a record's chain.
            e.HasIndex(x => new { x.RecordId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => x.Username);
            // Deliberately no foreign key to records: the audit trail must outlive any record row.
        });

        modelBuilder.Entity<AccessLogEntry>(e =>
        {
            e.ToTable("access_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50).IsRequired();
            e.Property(x => x.Details).HasMaxLength(1000);
            e.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.SubjectType, x.SubjectId });
        });

        modelBuilder.Entity<SystemDocument>(e =>
        {
            e.ToTable("system_documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.DocumentNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.MinimumReadRole).HasMaxLength(50).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(x => x.CurrentRevision).IsConcurrencyToken();
            e.HasIndex(x => x.DocumentNumber).IsUnique();
            e.HasMany(x => x.Revisions).WithOne().HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DocumentRevision>(e =>
        {
            e.ToTable("document_revisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.ChangeDescription).HasMaxLength(1000).IsRequired();
            e.Property(x => x.AuthorId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Author).HasMaxLength(200).IsRequired();
            e.Property(x => x.PreviousHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Hash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.DocumentId, x.RevisionNumber }).IsUnique();
        });
    }
}

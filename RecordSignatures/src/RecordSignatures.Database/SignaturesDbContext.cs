using Microsoft.EntityFrameworkCore;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database;

public class SignaturesDbContext(DbContextOptions<SignaturesDbContext> options) : DbContext(options)
{
    public const string RevokedStatus = "Revoked";

    public DbSet<SignerEntity> Signers => Set<SignerEntity>();

    public DbSet<SignatureEntity> Signatures => Set<SignatureEntity>();

    public DbSet<SignatureRevocationEntity> SignatureRevocations => Set<SignatureRevocationEntity>();

    public DbSet<SigningAttemptEntity> SigningAttempts => Set<SigningAttemptEntity>();

    public DbSet<SecurityAlertEntity> SecurityAlerts => Set<SecurityAlertEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC; Npgsql refuses non-UTC DateTimes for timestamptz.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SignerEntity>(e =>
        {
            e.ToTable("signers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.PrintedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.EmployeeId).HasMaxLength(200).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.StatusReason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.EnrolledByUserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.EnrolledByUsername).HasMaxLength(200).IsRequired();
            e.Property(x => x.IdentityMethod).HasMaxLength(50).IsRequired();
            e.Property(x => x.IdentityEvidence).HasMaxLength(500).IsRequired();
            e.Property(x => x.IdentityVerifiedByUserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.IdentityVerifiedByUsername).HasMaxLength(200).IsRequired();
            e.Property(x => x.CertificationVersion).HasMaxLength(20);
            e.Property(x => x.Version).IsConcurrencyToken();

            // 11.100(a): one signature per individual, never shared or reassigned. These stay unique even after revocation.
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.Username).IsUnique();
            // One live enrollment per person; a revoked person may be enrolled again under a new account.
            e.HasIndex(x => x.EmployeeId).IsUnique().HasFilter($"\"Status\" <> '{RevokedStatus}'");
            e.HasIndex(x => x.NextReviewDueUtc);
        });

        modelBuilder.Entity<SignatureEntity>(e =>
        {
            e.ToTable("signatures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RecordContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SignerUserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.SignerUsername).HasMaxLength(200).IsRequired();
            e.Property(x => x.SignerPrintedName).HasMaxLength(200).IsRequired();
            e.Property(x => x.MeaningCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.MeaningText).HasMaxLength(500).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.Property(x => x.PreviousSealHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SealKeyId).HasMaxLength(100).IsRequired();
            e.Property(x => x.SealHash).HasMaxLength(64).IsRequired();
            // The unique chain position stops two concurrent signers from forking a record's chain.
            e.HasIndex(x => new { x.RecordId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.SignerUserId);
            // Deliberately no foreign key to the signer: signatures must outlive any change to the signer row.
        });

        modelBuilder.Entity<SignatureRevocationEntity>(e =>
        {
            e.ToTable("signature_revocations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.RevokedByUserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.RevokedByUsername).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => x.SignatureId).IsUnique();
        });

        modelBuilder.Entity<SigningAttemptEntity>(e =>
        {
            e.ToTable("signing_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Username).HasMaxLength(200).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
            e.HasIndex(x => new { x.UserId, x.AttemptedUtc });
        });

        modelBuilder.Entity<SecurityAlertEntity>(e =>
        {
            e.ToTable("security_alerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Severity).HasMaxLength(20).IsRequired();
            e.Property(x => x.Type).HasMaxLength(50).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(200);
            e.Property(x => x.Username).HasMaxLength(200);
            e.Property(x => x.Detail).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => x.RaisedUtc);
        });
    }
}

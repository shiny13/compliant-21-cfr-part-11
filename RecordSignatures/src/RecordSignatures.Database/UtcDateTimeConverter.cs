using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RecordSignatures.Database;

/// <summary>Ensures every DateTime is written and read as UTC.</summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

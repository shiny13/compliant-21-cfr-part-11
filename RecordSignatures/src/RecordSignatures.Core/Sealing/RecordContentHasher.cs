using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Sealing;

/// <summary>Fingerprints exactly what a signer saw: the record's identity, type, title and fields (sorted) at one version.</summary>
public static class RecordContentHasher
{
    public static string Hash(RecordSnapshot record)
    {
        var parts = new List<string?> { "RSREC1", record.Id.ToString("D"), record.RecordType, record.Title, record.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var (name, value) in record.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            parts.Add(name);
            parts.Add(value);
        }
        return CanonicalHasher.Sha256(parts);
    }
}

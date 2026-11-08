using AuthenticationCore.Shared.Roles;

namespace RecordSignatures.Core.Models;

/// <summary>A controlled meaning of a signature (11.50(a)(3)) and the lowest role allowed to apply it.</summary>
public sealed record SignatureMeaning(string Code, string DisplayName, string Statement, UserRole MinimumRole);

/// <summary>
/// The closed list of signature meanings. A signer can only choose one of these, so the meaning shown with a
/// signature is always unambiguous. The statement text is copied into each signature when it is made.
/// </summary>
public static class MeaningCatalog
{
    public const string Authorship = "Authorship";
    public const string Review = "Review";
    public const string Approval = "Approval";
    public const string Responsibility = "Responsibility";

    public static IReadOnlyList<SignatureMeaning> All { get; } =
    [
        new(Authorship, "Authored", "I am the author of this record and attest that its content is accurate and complete.", UserRole.LabTechnician),
        new(Responsibility, "Responsible", "I accept responsibility for the content of this record and the work it describes.", UserRole.LabTechnician),
        new(Review, "Reviewed", "I have reviewed this record and found it accurate and complete.", UserRole.Reviewer),
        new(Approval, "Approved", "I approve this record for its intended use.", UserRole.LabManager),
    ];

    public static SignatureMeaning? Find(string? code) =>
        All.FirstOrDefault(m => string.Equals(m.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

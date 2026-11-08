namespace AuthenticationCore.Shared.Roles;

/// <summary>Default roles. A lower numeric value means a higher privilege level.</summary>
public enum UserRole
{
    Administrator = 1,
    LabManager = 2,
    Reviewer = 3,
    LabTechnician = 4,
}

public static class UserRoleExtensions
{
    public static string ToClaimValue(this UserRole role) => role.ToString();

    public static bool TryParse(string? value, out UserRole role) =>
        Enum.TryParse(value, ignoreCase: false, out role) && Enum.IsDefined(role);

    /// <summary>True when <paramref name="actual"/> is at least as privileged as <paramref name="required"/>.</summary>
    public static bool Satisfies(this UserRole actual, UserRole required) => (int)actual <= (int)required;

    /// <summary>Returns the most privileged role in the set, or null when none parse.</summary>
    public static UserRole? HighestOf(IEnumerable<string> claimValues)
    {
        UserRole? best = null;
        foreach (var value in claimValues)
        {
            if (TryParse(value, out var role) && (best is null || (int)role < (int)best.Value))
            {
                best = role;
            }
        }
        return best;
    }
}

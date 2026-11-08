using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Roles;
using Microsoft.AspNetCore.Authorization;

namespace AuthenticationCore.Shared.Authorization;

/// <summary>Each policy means "this role or any higher-privileged role".</summary>
public static class RolePolicies
{
    public const string Administrator = "Role.Administrator";
    public const string LabManager = "Role.LabManager";
    public const string Reviewer = "Role.Reviewer";
    public const string LabTechnician = "Role.LabTechnician";

    public static string For(UserRole role) => role switch
    {
        UserRole.Administrator => Administrator,
        UserRole.LabManager => LabManager,
        UserRole.Reviewer => Reviewer,
        UserRole.LabTechnician => LabTechnician,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

public sealed class MinimumRoleRequirement(UserRole minimum) : IAuthorizationRequirement
{
    public UserRole Minimum { get; } = minimum;
}

public sealed class MinimumRoleHandler : AuthorizationHandler<MinimumRoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumRoleRequirement requirement)
    {
        if (context.User.GetHighestRole() is { } role && role.Satisfies(requirement.Minimum))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}

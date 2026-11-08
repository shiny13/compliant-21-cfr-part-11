using AuthenticationCore.Service.Options;
using AuthenticationCore.Service.Security;

namespace AuthenticationCore.Service.Users;

public static class UserSeeder
{
    public static async Task SeedAsync(IUserStore store, PasswordHasher hasher, AuthServiceOptions options)
    {
        foreach (var seed in options.SeedUsers)
        {
            await store.AddAsync(new UserAccount(seed.Id ?? Guid.NewGuid(), seed.Username, hasher.Hash(seed.Password), seed.Roles.Distinct().ToArray()));
        }
    }
}

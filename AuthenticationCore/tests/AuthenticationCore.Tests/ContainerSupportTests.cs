using System.Net;
using AuthenticationCore.Service.Options;
using AuthenticationCore.Service.Security;
using AuthenticationCore.Service.Users;
using AuthenticationCore.Shared.Roles;
using AuthenticationCore.Tests.Support;

namespace AuthenticationCore.Tests;

/// <summary>Behaviour that container orchestration relies on: a health probe, and user ids that survive restarts.</summary>
public class ContainerSupportTests(AuthStack stack) : IClassFixture<AuthStack>
{
    [Fact]
    public async Task Health_is_anonymous_and_reports_healthy()
    {
        var response = await stack.Service.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync());
    }

    private static AuthServiceOptions Options(Guid? id) => new()
    {
        SeedUsers = [new SeedUser { Id = id, Username = "alice", Password = "pw-1", Roles = [UserRole.Reviewer] }],
    };

    [Fact]
    public async Task A_seed_user_with_a_fixed_id_gets_the_same_account_id_after_every_restart()
    {
        var id = Guid.NewGuid();
        for (var restart = 0; restart < 2; restart++)
        {
            var store = new InMemoryUserStore();
            await UserSeeder.SeedAsync(store, new PasswordHasher(), Options(id));
            Assert.Equal(id, (await store.FindByUsernameAsync("alice"))!.Id);
        }
    }

    [Fact]
    public async Task A_seed_user_without_an_id_gets_a_new_one()
    {
        var store = new InMemoryUserStore();
        await UserSeeder.SeedAsync(store, new PasswordHasher(), Options(null));
        Assert.NotEqual(Guid.Empty, (await store.FindByUsernameAsync("alice"))!.Id);
    }
}

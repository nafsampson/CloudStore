//summary//
// Checks account provisioning in memory and SQL migration generation without connecting to SQL Server.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Data;
using CloudStore.Models;
using CloudStore.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudStore.Tests;

public sealed class AccountSetupTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private ServiceProvider provider = null!;
    private readonly string password = "Aa1!" + Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync()
    {
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddRetailIdentity().AddDefaultTokenProviders();
        services.AddScoped<AccountProvisioner>();
        services.AddScoped<AccountPasswordReset>();
        provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() { await provider.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task First_administrator_is_enabled_hashed_and_can_authenticate()
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        Assert.True((await service.CreateAsync("first-admin", password, true, TestContext.Current.CancellationToken)).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var user = (await users.FindByNameAsync("FIRST-ADMIN"))!;
        Assert.Equal(AccessPolicies.AdminRole, user.Role);
        Assert.True(user.IsEnabled);
        Assert.True(user.LockoutEnabled);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(user, password));
        Assert.False(await users.CheckPasswordAsync(user, "wrong-password"));
        Assert.NotEmpty(user.SecurityStamp!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_administrator_blocks_bootstrap_even_when_disabled(bool enabled)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        Assert.True((await service.CreateAsync("existing-admin", password, true, TestContext.Current.CancellationToken)).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var existing = (await users.FindByNameAsync("existing-admin"))!;
        existing.IsEnabled = enabled;
        await users.UpdateAsync(existing);
        var hash = existing.PasswordHash;
        var result = await service.CreateAsync("replacement-admin", password, true, TestContext.Current.CancellationToken);
        Assert.Contains(result.Errors, error => error.Code == "AdministratorExists");
        Assert.Equal(1, await users.Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(hash, (await users.FindByNameAsync("existing-admin"))!.PasswordHash);
    }

    [Fact]
    public async Task Customer_creation_never_grants_administrator_role_and_names_are_case_insensitive()
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        Assert.True((await service.CreateAsync("customer", password, false, TestContext.Current.CancellationToken)).Succeeded);
        var result = await service.CreateAsync("CUSTOMER", password, false, TestContext.Current.CancellationToken);
        Assert.Contains(result.Errors, error => error.Code == "DuplicateUserName");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        Assert.Equal(AccessPolicies.CustomerRole, (await users.FindByNameAsync("customer"))!.Role);
        Assert.Equal(1, await users.Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.True((await service.CreateAsync("first-admin", password, true, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public async Task Invalid_username_creates_nothing(string username)
    {
        await using var scope = provider.CreateAsyncScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().CreateAsync(username, password, true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("onlylowercasepassword")]
    [InlineData("MissingSpecial123")]
    public async Task Weak_password_creates_nothing_and_does_not_consume_bootstrap(string weak)
    {
        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        Assert.False((await service.CreateAsync("first-admin", weak, true, TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.True((await service.CreateAsync("first-admin", password, true, TestContext.Current.CancellationToken)).Succeeded);
    }

    [Fact]
    public void Sql_server_migration_matches_current_model_and_generates_reviewable_idempotent_schema()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=invalid.example;Database=NeverOpened;Integrated Security=true").Options);
        Assert.False(database.Database.HasPendingModelChanges());
        var sql = database.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [LoginUsers]", sql);
        Assert.Contains("[PasswordHash]", sql);
        Assert.Contains("[AccessFailedCount]", sql);
        Assert.Contains("[IsEnabled]", sql);
        Assert.Contains("[Role]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [UserNameIndex]", sql);
        Assert.Contains("__EFMigrationsHistory", sql);
        Assert.DoesNotContain("INSERT INTO [LoginUsers]", sql);
        Assert.DoesNotContain("[Password]", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("CREATE DATABASE", sql);
    }

    [Fact]
    public async Task Invalid_command_arguments_are_rejected_before_database_access()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(2, await AccountSetupCommand.RunAsync(["bootstrap-admin", "password-argument"], configuration));
        Assert.Equal(2, await AccountSetupCommand.RunAsync(["reset-admin"], configuration));
        Assert.Equal(2, await AccountSetupCommand.RunAsync(["reset-password", "password-argument"], configuration));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Operator_reset_changes_password_and_stamp_without_changing_account_identity_or_other_accounts(bool administrator, bool enabled)
    {
        await using var scope = provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<AccountProvisioner>();
        Assert.True((await provisioning.CreateAsync("reset-target", password, administrator, TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await provisioning.CreateAsync("other-customer", password, false, TestContext.Current.CancellationToken)).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var target = (await users.FindByNameAsync("reset-target"))!;
        target.IsEnabled = enabled;
        target.AccessFailedCount = 5;
        target.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        Assert.True((await users.UpdateAsync(target)).Succeeded);
        var id = target.Id;
        var role = target.Role;
        var stamp = target.SecurityStamp;
        var hash = target.PasswordHash;
        var otherHash = (await users.FindByNameAsync("other-customer"))!.PasswordHash;
        var replacement = "New1!" + Guid.NewGuid().ToString("N");
        Assert.True((await scope.ServiceProvider.GetRequiredService<AccountPasswordReset>()
            .ResetAsync("RESET-TARGET", replacement, TestContext.Current.CancellationToken)).Succeeded);
        scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
        var updated = (await users.FindByNameAsync("reset-target"))!;
        Assert.Equal(id, updated.Id);
        Assert.Equal(role, updated.Role);
        Assert.Equal(enabled, updated.IsEnabled);
        Assert.NotEqual(hash, updated.PasswordHash);
        Assert.NotEqual(stamp, updated.SecurityStamp);
        Assert.False(await users.CheckPasswordAsync(updated, password));
        Assert.True(await users.CheckPasswordAsync(updated, replacement));
        Assert.Null(updated.LockoutEnd);
        Assert.Equal(0, updated.AccessFailedCount);
        Assert.Equal(otherHash, (await users.FindByNameAsync("other-customer"))!.PasswordHash);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("missingpunctuation123A")]
    public async Task Rejected_reset_preserves_the_existing_password_stamp_and_lockout(string weak)
    {
        await using var scope = provider.CreateAsyncScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().CreateAsync("customer", password, false, TestContext.Current.CancellationToken)).Succeeded);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var user = (await users.FindByNameAsync("customer"))!;
        user.AccessFailedCount = 5;
        user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        var stamp = user.SecurityStamp;
        var hash = user.PasswordHash;
        var lockout = user.LockoutEnd;
        Assert.False((await scope.ServiceProvider.GetRequiredService<AccountPasswordReset>().ResetAsync("customer", weak, TestContext.Current.CancellationToken)).Succeeded);
        scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
        user = (await users.FindByNameAsync("customer"))!;
        Assert.Equal(hash, user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal(lockout, user.LockoutEnd);
        Assert.Equal(5, user.AccessFailedCount);
    }

    [Fact]
    public async Task Reset_refuses_missing_accounts_and_passwords_longer_than_the_login_form_accepts()
    {
        await using var scope = provider.CreateAsyncScope();
        var reset = scope.ServiceProvider.GetRequiredService<AccountPasswordReset>();
        Assert.Contains((await reset.ResetAsync("missing", password, TestContext.Current.CancellationToken)).Errors, error => error.Code == "AccountNotFound");
        Assert.Contains((await reset.ResetAsync("missing", "Aa1!" + new string('x', 253), TestContext.Current.CancellationToken)).Errors, error => error.Code == "PasswordTooLong");
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Provisioned_customer_can_log_in_but_cannot_access_administrator_pages()
    {
        await using var app = new RetailFactory();
        await app.InitializeAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var provisioning = new AccountProvisioner(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>());
            Assert.True((await provisioning.CreateAsync("provisioned-customer", password, false, TestContext.Current.CancellationToken)).Succeeded);
        }
        using var browser = app.Browser();
        using var login = await app.Login(browser, "provisioned-customer", password);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/Customer", login.Headers.Location!.ToString());
        var restricted = await browser.GetAsync("/Admin", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, restricted.StatusCode);
    }
}
//-------------------End Of File------------------//

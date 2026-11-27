//summary//
// Explicitly creates local-only Identity accounts and fictional sample records; never seeds Azure startup.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Data;
using CloudStore.Models;
using CloudStore.Security;
using CloudStore.Uploads;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
namespace CloudStore.LocalDemo;

public static class DemoInitializer
{
    public static ServiceCollection AccountServices(DemoSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = settings.AccountsFile }.ToString()));
        services.AddRetailIdentity();
        services.AddScoped<AccountProvisioner>();
        return services;
    }
    public static async Task<bool> InitializeAsync(DemoSettings settings, string adminPassword, string customerPassword, CancellationToken cancellationToken = default)
    {
        if (File.Exists(settings.ReadyFile)) return false; // Rerunning never resets a password or clears data.
        Directory.CreateDirectory(settings.Directory);
        Directory.CreateDirectory(Path.Combine(settings.Directory, "uploads"));
        var records = new DemoDatabase(settings);
        records.Initialize();
        await using var provider = AccountServices(settings).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.EnsureCreatedAsync(cancellationToken);
        if (await database.Users.AnyAsync(cancellationToken))
            throw new InvalidOperationException("An unfinished local setup contains accounts. It will not be overwritten; review or back up that local directory first.");
        if (adminPassword == customerPassword) throw new InvalidOperationException("Choose different passwords for the two demo roles.");
        if (adminPassword.Length > 256 || customerPassword.Length > 256) throw new InvalidOperationException("Use passwords of at most 256 characters, matching the login form limit.");
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (name, role, password) in new[] { ("demo-admin", AccessPolicies.AdminRole, adminPassword), ("demo-customer", AccessPolicies.CustomerRole, customerPassword) })
        {
            var result = await users.CreateAsync(new LoginUser { UserName = name, Role = role, IsEnabled = true }, password);
            if (!result.Succeeded) throw new InvalidOperationException("Local account validation failed: " + string.Join(", ", result.Errors.Select(error => error.Code)));
        }
        // Seed stable fictional IDs only if absent, so a failed pre-commit setup can be retried safely.
        var uploads = new LocalUploads(settings);
        if (records.Read<Product>("Products").Count == 0)
        {
            await DemoCatalog.AddMissingAsync(settings, cancellationToken);
        }
        if ((await uploads.ListAsync(cancellationToken)).Count == 0)
            await uploads.SaveAsync(UploadPolicy.Validate("sample-contract.txt", "text/plain", "Fictional local demo contract. No personal or commercial data."u8.ToArray(), false), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await File.WriteAllTextAsync(settings.ReadyFile, "CloudStore local demo initialized; no Azure resources used.", cancellationToken);
        return true;
    }
}
//-------------------End Of File------------------//

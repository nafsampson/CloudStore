//summary//
// Runs account setup without starting the web host or constructing Azure clients.
//summary//
//-------------------Start Of File------------------//
using System.Text;
using CloudStore.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CloudStore.Security;

public static class AccountSetupCommand
{
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        var namedReset = args.Length == 3 && args[0] == "reset-password" && args[1] == "--username";
        if (!namedReset && (args.Length != 1 || args[0] is not ("bootstrap-admin" or "create-customer" or "reset-password")))
        {
            Console.Error.WriteLine("Usage: dotnet run -- bootstrap-admin | create-customer | reset-password [--username NAME]. Supply no passwords as command arguments.");
            return 2;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("Use an interactive terminal. Redirected input/output is refused to avoid storing passwords in files or logs.");
            return 2;
        }
        var connection = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Console.Error.WriteLine("Configure ConnectionStrings:DefaultConnection securely before account setup.");
            return 2;
        }
        // Deliberately separate from MVC: only SQL and Identity are registered here.
        await using var sql = new SqlConnection(connection);
        var accessToken = configuration["AccountSetup:SqlAccessToken"];
        if (!string.IsNullOrWhiteSpace(accessToken)) sql.AccessToken = accessToken;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(sql));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddRetailIdentity().AddDefaultTokenProviders();
        services.AddScoped<AccountProvisioner>();
        services.AddScoped<AccountPasswordReset>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        try
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Never apply migrations or create a database as a side effect of account setup.
            var applied = (await database.Database.GetAppliedMigrationsAsync()).ToArray();
            if (applied.Length == 0 || (await database.Database.GetPendingMigrationsAsync()).Any())
            {
                Console.Error.WriteLine("Apply the reviewed database migrations before managing accounts.");
                return 1;
            }
            string username;
            if (namedReset) username = args[2];
            else { Console.Write("Username: "); username = Console.ReadLine() ?? ""; }
            var resetting = args[0] == "reset-password";
            Console.Write(resetting ? "New password (hidden): " : "Password (hidden): ");
            var password = ReadPassword();
            Console.Write("Confirm password (hidden): ");
            var confirmation = ReadPassword();
            if (password is null || confirmation is null || password != confirmation)
            {
                Console.Error.WriteLine("No account changes made: passwords did not match or entry was cancelled.");
                return 1;
            }
            var result = resetting
                ? await scope.ServiceProvider.GetRequiredService<AccountPasswordReset>().ResetAsync(username, password)
                : await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().CreateAsync(username, password, args[0] == "bootstrap-admin");
            if (!result.Succeeded)
            {
                // Codes are enough to explain rejection without echoing submitted identifiers.
                Console.Error.WriteLine("Account changes not saved: " + string.Join(", ", result.Errors.Select(error => error.Code)));
                return 1;
            }
            Console.WriteLine(resetting
                ? "Password reset. Existing sessions are invalidated. Account role and data were preserved. No password was written to configuration or output."
                : "Account created. No password was written to configuration or output.");
            return 0;
        }
        catch (Exception exception) when (exception is Microsoft.Data.SqlClient.SqlException or DbUpdateException or InvalidOperationException)
        {
            // Do not dump connection strings, SQL statements, account fields or credentials.
            Console.Error.WriteLine("Account command failed. Check database availability, permissions and migration state. No migration or automatic recovery was attempted.");
            return 1;
        }
    }

    public static string? ReadPassword()
    {
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return buffer.ToString(); }
            if (key.Key == ConsoleKey.Escape) { Console.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar))
            {
                if (buffer.Length == 1024) { Console.WriteLine(); return null; }
                buffer.Append(key.KeyChar);
            }
        }
    }
}
//-------------------End Of File------------------//

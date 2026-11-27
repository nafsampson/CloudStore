//summary//
// Provides hidden-password local setup and customer creation without embedded credentials.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
namespace CloudStore.LocalDemo;

public static class DemoSetupCommand
{
    public static async Task<int> RunAsync(string[] args, DemoSettings settings)
    {
        if (args.Length != 1 || args[0] is not ("demo-setup" or "create-customer" or "demo-refresh-catalog"))
        { Console.Error.WriteLine("Use demo-setup, demo-refresh-catalog or create-customer with the LocalDemo profile. No password arguments are accepted."); return 2; }
        if (args[0] == "demo-refresh-catalog")
        {
            try { var added = await DemoCatalog.RefreshAsync(settings); Console.WriteLine($"Electronics catalog refreshed: {added} products added. Existing accounts, edited products and order/cart references preserved."); return 0; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or SqliteException or DbUpdateException)
            { Console.Error.WriteLine("Catalog refresh did not complete. Stop the website and check the local demo directory."); return 1; }
        }
        if (args[0] == "demo-setup" && File.Exists(settings.ReadyFile))
        { Console.WriteLine("Local demo already initialized. Existing accounts and data were preserved."); return 0; }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        { Console.Error.WriteLine("Use an interactive terminal for local account setup."); return 2; }
        try
        {
            if (args[0] == "demo-setup")
            {
                Console.WriteLine("Local-only demo. Choose separate passwords: at least 12 characters, uppercase, lowercase, number and punctuation.");
                var admin = Prompt("demo-admin");
                if (admin is null) return 1;
                var customer = Prompt("demo-customer");
                if (customer is null) return 1;
                await DemoInitializer.InitializeAsync(settings, admin, customer);
                Console.WriteLine("Local demo initialized. Sign in as demo-admin or demo-customer with the passwords you chose.");
            }
            else
            {
                settings.RequireReady();
                Console.Write("New customer username: "); var name = Console.ReadLine() ?? "";
                var password = Prompt("new customer"); if (password is null) return 1;
                await using var provider = DemoInitializer.AccountServices(settings).BuildServiceProvider();
                await using var scope = provider.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<AccountProvisioner>().CreateAsync(name, password, false);
                if (!result.Succeeded) { Console.Error.WriteLine("Customer not created: " + string.Join(", ", result.Errors.Select(error => error.Code))); return 1; }
                Console.WriteLine("Local customer created.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or SqliteException or DbUpdateException)
        { Console.Error.WriteLine("Local setup did not complete. Check password policy, write permissions and existing local data. No existing account was reset."); return 1; }
    }
    private static string? Prompt(string name)
    {
        Console.Write($"Password for {name} (hidden): "); var first = AccountSetupCommand.ReadPassword();
        if (first is null) return null;
        Console.Write("Confirm (hidden): "); var second = AccountSetupCommand.ReadPassword();
        if (second is not null && first == second) return first;
        Console.Error.WriteLine("Passwords did not match or entry was cancelled."); return null;
    }
}
//-------------------End Of File------------------//

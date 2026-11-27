//summary//
// Registers only local implementations; deliberately constructs no Azure SDK clients in demo mode.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Data;
using CloudStore.Orders;
using CloudStore.Services;
using CloudStore.Uploads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
namespace CloudStore.LocalDemo;

public static class DemoRegistration
{
    public static void AddLocalDemo(this IServiceCollection services, DemoSettings settings)
    {
        settings.RequireReady();
        services.AddSingleton(settings);
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = settings.AccountsFile, Mode = SqliteOpenMode.ReadWrite }.ToString()));
        services.AddSingleton<DemoDatabase>();
        services.AddSingleton<IRetailTables, LocalRetailTables>();
        services.AddSingleton<LocalUploads>();
        services.AddSingleton<IImageStore>(provider => provider.GetRequiredService<LocalUploads>());
        services.AddSingleton<IContractStore>(provider => provider.GetRequiredService<LocalUploads>());
        services.AddSingleton<LocalActivity>();
        services.AddSingleton<IOperationsLog>(provider => provider.GetRequiredService<LocalActivity>());
        services.AddSingleton<IUploadEvents>(provider => provider.GetRequiredService<LocalActivity>());
        services.AddSingleton<IOrderActivity>(provider => provider.GetRequiredService<LocalActivity>());
        services.AddSingleton<IOrderStore, LocalOrderStore>();
        services.AddSingleton<IOrderQueue, LocalOrderQueue>();
        services.AddSingleton<IProductLookup, LocalProductLookup>();
        services.AddSingleton<ICartStore, LocalCartStore>();
        services.AddScoped<OrderProcessor>();
        services.AddSingleton<LocalOrderPump>();
        services.AddHostedService<LocalOrderWorker>();
        services.AddDataProtection().SetApplicationName("CloudStore.LocalDemo")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "keys")));
    }
}
//-------------------End Of File------------------//

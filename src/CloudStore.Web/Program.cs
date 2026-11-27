//summary//
// Configures MVC, authentication and storage clients without provisioning Azure resources.
//summary//
//-------------------Start Of File------------------//
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Queues;
using CloudStore.Services;
using CloudStore.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using CloudStore.Models;
using CloudStore.Security;
using Microsoft.Extensions.Options;
using CloudStore.Orders;
using CloudStore.Uploads;
using CloudStore.LocalDemo;
using System.Net;
using CloudStore.Monitoring;

namespace CloudStore;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var mode = builder.Configuration["Storage:Mode"] ?? "Azure";
        if (mode is not ("Azure" or "LocalDemo")) throw new InvalidOperationException("Storage:Mode must be Azure or LocalDemo. There is no automatic fallback.");
        var local = mode == "LocalDemo";
        var demo = local ? DemoSettings.Create(builder.Environment.ContentRootPath, builder.Configuration, builder.Environment) : null;
        if (args.Length > 0 && !args[0].StartsWith("--"))
        {
            Environment.ExitCode = local
                ? await DemoSetupCommand.RunAsync(args, demo!)
                : await AccountSetupCommand.RunAsync(args, builder.Configuration);
            return;
        }
        await using var app = CreateApplication(builder);
        await app.RunAsync();
    }

    // Uses the same MVC pipeline for the running app and in-memory HTTP integration tests.
    public static WebApplication CreateApplication(WebApplicationBuilder builder)
    {
        var mode = builder.Configuration["Storage:Mode"] ?? "Azure";
        if (mode is not ("Azure" or "LocalDemo")) throw new InvalidOperationException("Storage:Mode must be Azure or LocalDemo. There is no automatic fallback.");
        var local = mode == "LocalDemo";
        var demo = local ? DemoSettings.Create(builder.Environment.ContentRootPath, builder.Configuration, builder.Environment) : null;
        if (local)
        {
            if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
                throw new InvalidOperationException("LocalDemo cannot use custom Kestrel endpoints.");
            var port = builder.Configuration.GetValue<int?>("LocalDemo:Port") ?? 5267;
            if (port is < 1024 or > 65535) throw new InvalidOperationException("LocalDemo:Port must be between 1024 and 65535.");
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddLocalDemo(demo!);
        }
        var cfg = builder.Configuration.GetSection("AzureStorage");
        builder.Services.AddCloudStoreMonitoring(builder.Configuration, local);
        builder.Services.AddHealthChecks();
        if (!local)
        {
            builder.Services.AddOptions<RuntimeConnections>().Configure<IConfiguration>((settings, configuration) =>
        {
                settings.Sql = configuration.GetConnectionString("DefaultConnection") ?? "";
                settings.Storage = configuration["AzureStorage:ConnectionString"] ?? "";
            }).Validate(settings => !string.IsNullOrWhiteSpace(settings.Sql), "ConnectionStrings:DefaultConnection is required.")
              .Validate(settings => !string.IsNullOrWhiteSpace(settings.Storage), "AzureStorage:ConnectionString is required.")
              .Validate(settings => settings.HasValidStorage(), "AzureStorage:ConnectionString must be a complete storage connection string, not an account key.")
              .ValidateOnStart();
            builder.Services.AddDbContext<AppDbContext>((services, options) => options.UseSqlServer(
                services.GetRequiredService<IOptions<RuntimeConnections>>().Value.Sql));
        }
        builder.Services.AddRetailIdentity().AddSignInManager().AddClaimsPrincipalFactory<RetailClaimsFactory>();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        builder.Services.AddScoped<RetailCookieEvents>();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            // A separate cookie name prevents older plaintext-login sessions being accepted.
            options.Cookie.Name = local ? "CloudStore.LocalDemo.Auth" : "__Host-CloudStore.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.LoginPath = "/Login/Index";
            options.AccessDeniedPath = "/Login/AccessDenied";
            options.EventsType = typeof(RetailCookieEvents);
        });
        builder.Services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
            options.AddPolicy(AccessPolicies.RetailUser, policy => policy.RequireAuthenticatedUser()
                .RequireRole(AccessPolicies.AdminRole, AccessPolicies.CustomerRole));
            options.AddPolicy(AccessPolicies.Administrator, policy => policy.RequireAuthenticatedUser()
                .RequireRole(AccessPolicies.AdminRole));
            options.AddPolicy(AccessPolicies.Customer, policy => policy.RequireAuthenticatedUser()
                .RequireRole(AccessPolicies.CustomerRole));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                }));
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = local ? "CloudStore.LocalDemo.Antiforgery" : "__Host-CloudStore.Antiforgery";
            options.Cookie.SecurePolicy = local ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        builder.Services.AddHttpContextAccessor();
        if (!local)
        {
            builder.Services.AddSingleton(services => new TableServiceClient(services.GetRequiredService<IOptions<RuntimeConnections>>().Value.Storage));
            builder.Services.AddSingleton(services => new BlobServiceClient(services.GetRequiredService<IOptions<RuntimeConnections>>().Value.Storage));
            builder.Services.AddSingleton(services => new ShareClient(services.GetRequiredService<IOptions<RuntimeConnections>>().Value.Storage,
                cfg["FileShareName"] ?? "contracts"));
            // Unqualified queue injection means operations; order senders own a separate order client.
            builder.Services.AddSingleton(services => new QueueClient(services.GetRequiredService<IOptions<RuntimeConnections>>().Value.Storage,
                cfg["QueueName"] ?? "operations"));
            builder.Services.AddSingleton<IRetailTables, AzureRetailTables>();
            builder.Services.AddSingleton<IOperationsLog, AzureOperationsLog>();
            builder.Services.AddSingleton<IImageStore, AzureImageStore>();
            builder.Services.AddSingleton<IUploadEvents, AzureUploadEvents>();

            builder.Services.AddSingleton<IContractStore, AzureContractStore>();
        }
        builder.Services.AddSingleton(TimeProvider.System);
        if (!local)
        {
            builder.Services.AddScoped<IOrderStore, AzureOrderStore>();
            builder.Services.AddScoped<IOrderQueue, AzureOrderQueue>();
            builder.Services.AddScoped<IProductLookup, AzureProductLookup>();
            builder.Services.AddScoped<IOrderActivity, AzureOrderActivity>();
            builder.Services.AddScoped<ICartStore, AzureCartStore>();
        }
        builder.Services.AddScoped<OrderDispatcher>();
        builder.Services.AddScoped<IOrderRecovery>(services => services.GetRequiredService<OrderDispatcher>());
        builder.Services.AddScoped<CheckoutService>();
        builder.Services.AddScoped<IOrderAccess, OrderAccess>();
        builder.Services.AddControllersWithViews(options =>
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

        var app = builder.Build();
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }
        if (local)
        {
            app.Use(async (context, next) =>
            {
                if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
                { context.Response.StatusCode = 403; return; }
                await next();
            });
        }
        else app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        // Tables, containers, queues and shares must be provisioned separately.
        app.MapDefaultControllerRoute();
        // Liveness only: does not query SQL/storage or wake a paused database.
        app.MapHealthChecks("/health/live").AllowAnonymous();
        return app;
    }

}
//-------------------End Of File------------------//

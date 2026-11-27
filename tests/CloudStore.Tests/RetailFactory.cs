//summary//
// Exercises real MVC middleware, Identity and cookies without opening network listeners or contacting Azure.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Queues;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Identity;
using CloudStore.Data;
using CloudStore.Models;
using CloudStore.Security;
using CloudStore.Services;
using CloudStore.Orders;
using CloudStore.Uploads;
using Xunit;

namespace CloudStore.Tests;

public class RetailFactory : WebApplicationFactory<CloudStore.Program>, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private int _clientNumber;
    public TestTables Tables { get; } = new();
    public CapturedOrders Orders { get; } = new();
    public MemoryUploads Uploads { get; } = new();
    public string Password { get; } = "Aa1!" + Guid.NewGuid().ToString("N");
    public Dictionary<string, int> AccountIds { get; } = new();
    public virtual OrderHarness? Pipeline => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Data Source=unused-test-database",
            ["AzureStorage:ConnectionString"] = "DefaultEndpointsProtocol=https;AccountName=example;AccountKey="
                + Convert.ToBase64String(new byte[64]) + ";EndpointSuffix=core.windows.net"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IImageStore>();
            services.RemoveAll<IContractStore>();
            services.RemoveAll<IUploadEvents>();
            services.AddSingleton<IImageStore>(Uploads);
            services.AddSingleton<IContractStore>(Uploads);
            services.AddSingleton<IUploadEvents>(Uploads);
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            services.RemoveAll<TableServiceClient>();
            services.AddSingleton<TableServiceClient>(Tables);
            services.RemoveAll<BlobServiceClient>();
            services.AddSingleton(new BlobServiceClient("UseDevelopmentStorage=true",
                new BlobClientOptions { Transport = new NoNetworkTransport() }));
            services.RemoveAll<QueueClient>();
            services.AddSingleton(new QueueClient("UseDevelopmentStorage=true", "operations",
                new QueueClientOptions { Transport = new NoNetworkTransport() }));
            services.RemoveAll<ShareClient>();
            services.AddSingleton(new ShareClient(new Uri("https://invalid.example/contracts"),
                new ShareClientOptions { Transport = new NoNetworkTransport() }));
            services.RemoveAll<IOrderAccess>();
            services.AddSingleton<IOrderAccess>(Orders);
            services.RemoveAll<IOrderRecovery>();
            services.AddSingleton<IOrderRecovery>(Orders);
            if (Pipeline is { } pipeline)
            {
                services.RemoveAll<IOrderAccess>();
                services.AddSingleton<IOrderAccess>(pipeline.Orders);
                services.RemoveAll<IOrderRecovery>();
                services.AddSingleton<IOrderRecovery>(pipeline.Dispatcher);
                services.RemoveAll<CheckoutService>();
                services.AddSingleton(pipeline.Checkout);
            }
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.AddTransient<IStartupFilter, TestClientAddress>();
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        foreach (var (name, role) in new[]
        {
            ("customer-a", AccessPolicies.CustomerRole), ("customer-b", AccessPolicies.CustomerRole),
            ("administrator", AccessPolicies.AdminRole), ("lockout-user", AccessPolicies.CustomerRole),
            ("disabled-user", AccessPolicies.CustomerRole), ("invalid-role", "Owner"),
            ("changed-role", AccessPolicies.AdminRole), ("changed-password", AccessPolicies.CustomerRole),
            ("disabled-after-login", AccessPolicies.CustomerRole)
        })
        {
            var user = new LoginUser { UserName = name, Role = role, IsEnabled = name != "disabled-user" };
            var result = await users.CreateAsync(user, Password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(";", result.Errors.Select(error => error.Description)));
            AccountIds[name] = user.Id;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public HttpClient Browser()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost"), HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("X-Test-Address", "10.0.0." + Interlocked.Increment(ref _clientNumber));
        return client;
    }

    public static async Task<string> Token(HttpClient client, string path = "/Login/Index")
    {
        var html = await client.GetStringAsync(path, TestContext.Current.CancellationToken);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Antiforgery token was not rendered.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public async Task<HttpResponseMessage> Login(HttpClient client, string username, string? password = null)
    {
        var token = await Token(client);
        return await client.PostAsync("/Login/Index", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password ?? Password, ["__RequestVerificationToken"] = token
        }), TestContext.Current.CancellationToken);
    }

    private sealed class TestClientAddress : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                // Only the test assembly trusts this header; production never reads it.
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Address"], out var address))
                    context.Connection.RemoteIpAddress = address;
                await proceed();
            });
            next(app);
        };
    }
}

public sealed class CapturedOrders : IOrderAccess, IOrderRecovery
{
    public List<Order> Submitted { get; } = new();
    public List<Order> Visible { get; } = new();
    public int ProcessCalls { get; private set; }
    public Task<List<Order>> ListAsync(string accountId, bool administrator) =>
        Task.FromResult(Visible.Where(order => administrator || order.CustomerId == accountId).ToList());
    public Task<SubmissionResult> SubmitAsync(Order order) { Submitted.Add(order); return Task.FromResult(new SubmissionResult(order, true)); }
    public Task<bool> ProcessAsync(string orderId) { ProcessCalls++; return Task.FromResult(true); }
    public Task<DispatchResult> RetryAsync(string id) => Task.FromResult(new DispatchResult(true, "Sent"));
    public Task<RecoveryResult> RetryPendingAsync(int limit) => Task.FromResult(new RecoveryResult(0, 0, 0));
}
//-------------------End Of File------------------//

//summary//
// Runs the real local mode against disposable disk SQLite/files, without Azure or a network listener.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using System.Net.Http.Headers;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Azure.Storage.Files.Shares;
using CloudStore.Data;
using CloudStore.LocalDemo;
using CloudStore.Models;
using CloudStore.Orders;
using CloudStore.Services;
using CloudStore.Uploads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CloudStore.Tests;

public sealed class DemoDirectory : IAsyncDisposable
{
    public static string ProjectRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/CloudStore.Web"));
    public string Relative { get; } = ".local-demo/tests/" + Guid.NewGuid().ToString("N");
    public DemoSettings Settings => new(Path.Combine(ProjectRoot, Relative));
    public string AdminPassword { get; } = "Aa1!" + Guid.NewGuid().ToString("N");
    public string CustomerPassword { get; } = "Bb2!" + Guid.NewGuid().ToString("N");
    public Task<bool> Initialize() => DemoInitializer.InitializeAsync(Settings, AdminPassword, CustomerPassword, TestContext.Current.CancellationToken);
    public WebApplicationBuilder Builder(string environment = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(CloudStore.Program).Assembly.FullName, ContentRootPath = ProjectRoot, EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Mode"] = "LocalDemo", ["LocalDemo:Directory"] = Relative });
        builder.WebHost.UseTestServer(); return builder;
    }
    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(Settings.Directory);
        var allowed = Path.Combine(ProjectRoot, ".local-demo", "tests") + Path.DirectorySeparatorChar;
        if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        return ValueTask.CompletedTask;
    }
}

public sealed class LocalDemoStorageTests
{
    [Fact]
    public async Task Explicit_setup_is_repeatable_without_resetting_accounts_and_persists_sample_data()
    {
        await using var directory = new DemoDirectory();
        Assert.True(await directory.Initialize());
        Assert.False(await DemoInitializer.InitializeAsync(directory.Settings, "unused", "unused", TestContext.Current.CancellationToken));
        await using var accounts = DemoInitializer.AccountServices(directory.Settings).BuildServiceProvider();
        await using var scope = accounts.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var admin = (await users.FindByNameAsync("demo-admin"))!;
        Assert.True(await users.CheckPasswordAsync(admin, directory.AdminPassword));
        Assert.Equal(2, await users.Users.CountAsync(TestContext.Current.CancellationToken));
        var db = new DemoDatabase(directory.Settings);
        var products = db.Read<Product>("Products");
        Assert.Equal(6, products.Count);
        foreach (var product in products)
        {
            Assert.True(UploadPolicy.StoredName(product.ImageName, true));
            var image = await new LocalUploads(directory.Settings).ReadAsync(product.ImageName!, TestContext.Current.CancellationToken);
            Assert.NotNull(image);
            await image.Content.DisposeAsync();
        }
        Assert.Empty(db.Read<Customer>("Customers"));
        Assert.Single(await new LocalUploads(directory.Settings).ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Catalog_refresh_preserves_edited_products_and_referenced_legacy_items()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var db = new DemoDatabase(directory.Settings);
        var headphones = db.Read<Product>("Products").Single(product => product.RowKey == "demo-headphones");
        var originalImage = headphones.ImageName;
        db.Update("Products", new TableEntity("Product", headphones.RowKey) { ["Price"] = "950.00" }, headphones.ETag, TableUpdateMode.Merge);
        db.Add("Products", new Product { RowKey = "demo-coffee", Name = "Sample coffee beans", Sku = "DEMO-COFFEE", Price = "129.95" });
        db.Add("Products", new Product { RowKey = "demo-tote", Name = "Sample canvas tote", Sku = "DEMO-TOTE", Price = "89.50" });
        db.Add("Cart", new CartItem { PartitionKey = "1", RowKey = "legacy-cart", ProductId = "demo-coffee", Quantity = 1 });
        Assert.Equal(0, await DemoCatalog.RefreshAsync(directory.Settings, TestContext.Current.CancellationToken));
        var products = db.Read<Product>("Products");
        Assert.Equal("950.00", products.Single(product => product.RowKey == "demo-headphones").Price);
        Assert.Equal(originalImage, products.Single(product => product.RowKey == "demo-headphones").ImageName);
        Assert.Contains(products, product => product.RowKey == "demo-coffee");
        Assert.DoesNotContain(products, product => product.RowKey == "demo-tote");
        Assert.Single(db.Read<CartItem>("Cart"));
    }

    [Fact]
    public async Task Catalog_refresh_repairs_missing_private_images_without_changing_product_details()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var db = new DemoDatabase(directory.Settings);
        var product = db.Read<Product>("Products").Single(item => item.RowKey == "demo-speaker");
        db.Update("Products", new TableEntity("Product", product.RowKey) { ["ImageName"] = "", ["Price"] = "620.00" }, product.ETag, TableUpdateMode.Merge);
        Assert.Equal(0, await DemoCatalog.RefreshAsync(directory.Settings, TestContext.Current.CancellationToken));
        var repaired = db.Read<Product>("Products").Single(item => item.RowKey == product.RowKey);
        Assert.Equal("620.00", repaired.Price);
        Assert.True(UploadPolicy.StoredName(repaired.ImageName, true));
        Assert.NotEqual(product.ImageName, repaired.ImageName);
        var image = await new LocalUploads(directory.Settings).ReadAsync(repaired.ImageName!, TestContext.Current.CancellationToken);
        Assert.NotNull(image);
        await image.Content.DisposeAsync();
    }

    [Fact]
    public async Task Invalid_second_password_rolls_back_both_accounts_and_allows_clean_retry()
    {
        await using var directory = new DemoDirectory();
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoInitializer.InitializeAsync(directory.Settings, directory.AdminPassword, "weak", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(directory.Settings.ReadyFile));
        await using (var provider = DemoInitializer.AccountServices(directory.Settings).BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.CountAsync(TestContext.Current.CancellationToken));
        }
        Assert.True(await directory.Initialize());
    }

    [Fact]
    public async Task SQLite_inserts_are_unique_and_stale_versions_cannot_overwrite_or_remove_newer_data()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var db = new DemoDatabase(directory.Settings);
        var first = db.Read<Product>("Products").First();
        var oldVersion = first.ETag;
        var duplicate = Assert.Throws<RequestFailedException>(() => db.Add("Products", first)); Assert.Equal(409, duplicate.Status);
        db.Update("Products", new TableEntity("Product", first.RowKey) { ["Name"] = "Updated" }, oldVersion, TableUpdateMode.Merge);
        Assert.Equal(412, Assert.Throws<RequestFailedException>(() => db.Update("Products", first, oldVersion, TableUpdateMode.Replace)).Status);
        Assert.Equal(412, Assert.Throws<RequestFailedException>(() => db.Delete("Products", "Product", first.RowKey, oldVersion)).Status);
        var reopened = new DemoDatabase(directory.Settings).Read<Product>("Products").Single(product => product.RowKey == first.RowKey);
        Assert.Equal("Updated", reopened.Name); Assert.Equal(first.Price, reopened.Price);
    }

    [Fact]
    public async Task Local_cart_cleanup_respects_owner_and_missing_row_is_idempotent()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var db = new DemoDatabase(directory.Settings);
        db.Add("Cart", new CartItem { PartitionKey = "1", RowKey = "shared", ProductId = "demo-coffee", Quantity = 1 });
        db.Add("Cart", new CartItem { PartitionKey = "2", RowKey = "shared", ProductId = "demo-notebook", Quantity = 1 });
        var store = new LocalCartStore(db);
        var one = Assert.Single(await store.ReadAsync("1"));
        await store.RemoveAsync("1", one.RowKey, one.ETag); await store.RemoveAsync("1", one.RowKey, one.ETag);
        Assert.Empty(await store.ReadAsync("1")); Assert.Single(await store.ReadAsync("2"));
    }

    [Fact]
    public async Task Queue_leases_survive_restart_and_stop_claiming_after_five_attempts()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var db = new DemoDatabase(directory.Settings); var now = DateTimeOffset.UtcNow;
        db.Enqueue("orders", "invalid reference");
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var claimed = new DemoDatabase(directory.Settings).Claim(now);
            Assert.NotNull(claimed); Assert.Equal(attempt, claimed.Value.Attempts);
            Assert.Null(db.Claim(now)); now = now.AddSeconds(31);
        }
        Assert.Null(db.Claim(now));
    }

    [Fact]
    public async Task Local_files_are_generated_create_only_and_partial_names_are_not_listed()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        var files = new LocalUploads(directory.Settings);
        var upload = UploadPolicy.Validate("terms.txt", "text/plain", "Terms"u8.ToArray(), false);
        await files.SaveAsync(upload, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => files.SaveAsync(upload, TestContext.Current.CancellationToken));
        Assert.Null(await files.ReadAsync("../accounts.db", TestContext.Current.CancellationToken));
        var downloaded = await files.ReadAsync(upload.Name, TestContext.Current.CancellationToken);
        await using var content = downloaded!.Content;
        using var reader = new StreamReader(content); Assert.Equal("Terms", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(directory.Settings.Directory, "uploads", "unfinished.upload"), "incomplete", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("unfinished.upload", await files.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Local_mode_is_rejected_outside_development(string environment)
    {
        await using var directory = new DemoDirectory();
        Assert.Throws<InvalidOperationException>(() => CloudStore.Program.CreateApplication(directory.Builder(environment)));
        Assert.False(Directory.Exists(directory.Settings.Directory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public async Task Local_preview_port_rejects_values_outside_unprivileged_range(int port)
    {
        await using var directory = new DemoDirectory();
        var builder = directory.Builder();
        builder.Configuration["LocalDemo:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var error = Assert.Throws<InvalidOperationException>(() => CloudStore.Program.CreateApplication(builder));
        Assert.Contains("LocalDemo:Port", error.Message);
    }

    [Fact]
    public async Task Missing_setup_and_escaping_data_path_fail_closed_without_creating_Azure_clients()
    {
        await using var directory = new DemoDirectory();
        Assert.Throws<InvalidOperationException>(() => CloudStore.Program.CreateApplication(directory.Builder()));
        var builder = directory.Builder(); builder.Configuration["LocalDemo:Directory"] = "../outside";
        Assert.Throws<InvalidOperationException>(() => CloudStore.Program.CreateApplication(builder));
        Assert.False(Directory.Exists(directory.Settings.Directory));
    }
}

public sealed class LocalDemoHttpTests
{
    private sealed class Cookies(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer jar = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookie = jar.GetCookieHeader(request.RequestUri!);
            if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) jar.SetCookies(request.RequestUri!, value);
            return response;
        }
    }
    private static HttpClient Browser(WebApplication app) => new(new Cookies(app.GetTestServer().CreateHandler())) { BaseAddress = new Uri("http://127.0.0.1:5267") };
    private static async Task<HttpResponseMessage> Post(HttpClient browser, string path, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await RetailFactory.Token(browser);
        return await browser.PostAsync(path, new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
    }
    private static Task<HttpResponseMessage> Login(HttpClient browser, string username, string password) => Post(browser, "/Login/Index", new() { ["username"] = username, ["password"] = password });

    [Fact]
    public async Task Employer_can_use_login_products_cart_checkout_worker_uploads_and_reopen_data_without_Azure()
    {
        await using var directory = new DemoDirectory(); await directory.Initialize();
        await using (var app = CloudStore.Program.CreateApplication(directory.Builder()))
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            Assert.Null(app.Services.GetService<TableServiceClient>()); Assert.Null(app.Services.GetService<BlobServiceClient>());
            Assert.Null(app.Services.GetService<QueueClient>()); Assert.Null(app.Services.GetService<ShareClient>());
            Assert.IsType<LocalOrderStore>(app.Services.GetRequiredService<IOrderStore>());
            using var customer = Browser(app); using var admin = Browser(app);
            Assert.Equal(HttpStatusCode.Redirect, (await Login(customer, "demo-customer", directory.CustomerPassword)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Login(admin, "demo-admin", directory.AdminPassword)).StatusCode);
            var products = await customer.GetStringAsync("/Tables/Products", TestContext.Current.CancellationToken);
            Assert.Contains("Local demo", products); Assert.Contains("Wireless Over-Ear Headphones", products);
            Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/Files", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Post(customer, "/Cart/Add", new() { ["productId"] = "demo-headphones" })).StatusCode);
            Assert.Contains("Wireless Over-Ear Headphones", await customer.GetStringAsync("/Cart", TestContext.Current.CancellationToken));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(customer, "/Cart/Checkout", new())).StatusCode);
            var orderStore = app.Services.GetRequiredService<IOrderStore>();
            Order? order = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                order = (await orderStore.ListAsync("", true)).SingleOrDefault();
                if (order?.Status == OrderRules.Ready) break;
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(order); Assert.Equal(OrderRules.Ready, order.Status); Assert.Equal(89900, order.TotalPriceCents);
            await Post(customer, "/Cart/Checkout", new()); Assert.Single(await orderStore.ListAsync("", true));
            Assert.Equal(HttpStatusCode.Redirect, (await Post(admin, "/Orders/Process", new() { ["orderId"] = order.RowKey })).StatusCode);
            Assert.Equal(OrderRules.Processed, (await orderStore.GetAsync(order.RowKey))!.Status);
            var image = await customer.GetAsync("/Tables/Image?id=demo-headphones", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, image.StatusCode); Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(await RetailFactory.Token(admin)), "__RequestVerificationToken");
            var file = new ByteArrayContent("A local test contract"u8.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); form.Add(file, "file", "review.txt");
            Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/Files/Upload", form, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Contains("Upload saved", await admin.GetStringAsync("/Queue", TestContext.Current.CancellationToken));
            Assert.Equal(2, (await app.Services.GetRequiredService<IContractStore>().ListAsync(TestContext.Current.CancellationToken)).Count);
            var noToken = await customer.PostAsync("/Cart/Checkout", new FormUrlEncodedContent(new Dictionary<string, string>()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
        await using var restarted = CloudStore.Program.CreateApplication(directory.Builder());
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OrderRules.Processed, Assert.Single(await restarted.Services.GetRequiredService<IOrderStore>().ListAsync("", true)).Status);
        using var browser = Browser(restarted); Assert.Equal(HttpStatusCode.Redirect, (await Login(browser, "demo-customer", directory.CustomerPassword)).StatusCode);
        await restarted.StopAsync(TestContext.Current.CancellationToken);
    }
}
//-------------------End Of File------------------//

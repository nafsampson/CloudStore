//summary//
// Verifies real cookie/antiforgery flows and role/ownership boundaries without Azure or SQL Server.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CloudStore.Data;
using CloudStore.Models;
using CloudStore.Security;
using CloudStore.Services;
using CloudStore.Orders;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace CloudStore.Tests;

public class SecurityTests(RetailFactory app) : IClassFixture<RetailFactory>
{
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path,
        Dictionary<string, string>? fields = null, string? token = null)
    {
        fields ??= new();
        if (path == "/Orders/Create" && !fields.ContainsKey("RequestId")) fields["RequestId"] = Guid.NewGuid().ToString("D");
        fields["__RequestVerificationToken"] = token ?? await RetailFactory.Token(client);
        return await client.PostAsync(path, new FormUrlEncodedContent(fields), TestContext.Current.CancellationToken);
    }

    private async Task<HttpClient> SignedIn(string username = "customer-a")
    {
        var client = app.Browser();
        using var login = await app.Login(client, username);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    [Theory]
    [InlineData("customer-a", "/Customer")]
    [InlineData("administrator", "/Admin")]
    [InlineData("CUSTOMER-A", "/Customer")]
    public async Task Valid_hashed_login_creates_secure_cookie_and_correct_dashboard(string username, string dashboard)
    {
        using var client = app.Browser();
        using var login = await app.Login(client, username);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith(dashboard, login.Headers.Location!.ToString());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), item => item.StartsWith("__Host-CloudStore.Auth="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(dashboard, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Account_passwords_are_salted_hashes_and_plaintext_has_no_model_property()
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        var first = (await users.FindByNameAsync("customer-a"))!;
        var second = (await users.FindByNameAsync("customer-b"))!;
        Assert.NotEqual(app.Password, first.PasswordHash);
        Assert.NotEqual(first.PasswordHash, second.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(first, app.Password));
        Assert.False(await users.CheckPasswordAsync(first, "Incorrect-password"));
        Assert.Null(typeof(LoginUser).GetProperty("Password"));
    }

    [Theory]
    [InlineData("does-not-exist", false)]
    [InlineData("disabled-user", true)]
    [InlineData("invalid-role", true)]
    [InlineData("customer-a", false)]
    public async Task Invalid_credentials_disabled_accounts_and_unknown_roles_get_generic_errors(string username, bool correctPassword)
    {
        using var client = app.Browser();
        using var response = await app.Login(client, username, correctPassword ? app.Password : "Incorrect-password");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Unable to sign in. Check your details or try again later.", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(cookie => cookie.StartsWith("__Host-CloudStore.Auth=")));
    }

    [Fact]
    public async Task Five_failed_attempts_persist_lockout_and_block_correct_password()
    {
        using var client = app.Browser();
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await app.Login(client, "lockout-user", "Incorrect-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Login(client, "lockout-user")).StatusCode);
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("lockout-user"))!));
    }

    [Fact]
    public async Task Login_throttles_repeated_requests_from_one_address()
    {
        using var client = app.Browser();
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(HttpStatusCode.OK, (await app.Login(client, "unknown-throttle-account")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.Login(client, "unknown-throttle-account")).StatusCode);
    }

    [Fact]
    public async Task Login_rejects_missing_antiforgery_token()
    {
        using var client = app.Browser();
        var response = await client.PostAsync("/Login/Index", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["username"] = "customer-a", ["password"] = app.Password }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/Admin")]
    [InlineData("/Customer")]
    [InlineData("/Tables/Products")]
    [InlineData("/Files")]
    [InlineData("/Queue")]
    [InlineData("/Orders")]
    [InlineData("/Cart")]
    public async Task Anonymous_requests_are_challenged(string path)
    {
        using var client = app.Browser();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Login/Index", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Admin")]
    [InlineData("/Tables/Index")]
    [InlineData("/Tables/EditProduct?rowKey=foreign")]
    [InlineData("/Files")]
    [InlineData("/Files/Download?name=contract.pdf")]
    [InlineData("/Queue")]
    public async Task Customer_cannot_open_administrator_routes(string path)
    {
        using var client = await SignedIn();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/Tables/AddProduct")]
    [InlineData("/Tables/DeleteProduct")]
    [InlineData("/Tables/EditProduct")]
    [InlineData("/Files/Upload")]
    [InlineData("/Queue/Send")]
    [InlineData("/Orders/Process")]
    [InlineData("/Orders/RetryDelivery")]
    [InlineData("/Orders/RetryPending")]
    public async Task Customer_cannot_call_administrator_mutations_even_with_valid_antiforgery(string path)
    {
        using var client = await SignedIn();
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(client, path)).StatusCode);
    }

    [Theory]
    [InlineData("/Customer")]
    [InlineData("/Cart")]
    [InlineData("/Orders/Create")]
    public async Task Administrator_cannot_impersonate_a_customer_workflow(string path)
    {
        using var client = await SignedIn("administrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("customer-a")]
    [InlineData("administrator")]
    public async Task Both_roles_can_browse_products(string name)
    {
        using var client = await SignedIn(name);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Tables/Products", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/Tables/Customers")]
    [InlineData("/Tables/CustomerDetails?id=foreign")]
    [InlineData("/Tables/EditCustomer?rowKey=foreign")]
    public async Task Removed_customer_directory_routes_are_not_available(string path)
    {
        using var client = await SignedIn("administrator");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/Tables/AddProduct")]
    [InlineData("/Files/Upload")]
    [InlineData("/Queue/Send")]
    [InlineData("/Orders/Process")]
    [InlineData("/Orders/RetryDelivery")]
    [InlineData("/Orders/RetryPending")]
    public async Task Administrator_mutations_require_antiforgery(string path)
    {
        using var client = await SignedIn("administrator");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("/Cart/Add")]
    [InlineData("/Cart/Remove")]
    [InlineData("/Cart/Checkout")]
    [InlineData("/Orders/Create")]
    [InlineData("/Login/Logout")]
    public async Task Customer_mutations_require_antiforgery(string path)
    {
        using var client = await SignedIn();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>()), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Tokens_cannot_be_reused_by_another_account()
    {
        using var first = await SignedIn("customer-a");
        using var second = await SignedIn("customer-b");
        var token = await RetailFactory.Token(first);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(second, "/Orders/Create", new()
        { ["ProductId"] = "product", ["Quantity"] = "1" }, token)).StatusCode);
    }

    [Fact]
    public async Task Logout_is_post_only_and_removes_authentication()
    {
        using var client = await SignedIn();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/Login/Logout", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Customer", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Login/Logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Customer", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Forged_order_owner_status_and_keys_are_ignored()
    {
        using var client = await SignedIn();
        var response = await Post(client, "/Orders/Create", new()
        {
            ["ProductId"] = "test-product", ["Quantity"] = "2", ["CustomerId"] = "foreign-owner",
            ["RowKey"] = "forged-key", ["PartitionKey"] = "forged-partition", ["Status"] = "Processed"
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var order = app.Orders.Submitted.Last();
        Assert.Equal(app.AccountIds["customer-a"].ToString(), order.CustomerId);
        Assert.Equal("Pending", order.Status);
        Assert.Equal("Order", order.PartitionKey);
        Assert.NotEqual("forged-key", order.RowKey);
        Assert.Equal(2, order.Quantity);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    [InlineData("not-a-number")]
    public async Task Invalid_order_quantity_never_reaches_storage(string quantity)
    {
        using var client = await SignedIn();
        var before = app.Orders.Submitted.Count;
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "/Orders/Create", new()
        { ["ProductId"] = "test-product", ["Quantity"] = quantity })).StatusCode);
        Assert.Equal(before, app.Orders.Submitted.Count);
    }

    [Fact]
    public async Task Customer_cannot_select_a_different_owner_in_order_listing()
    {
        app.Orders.Visible.Clear();
        app.Orders.Visible.Add(new Order { CustomerId = app.AccountIds["customer-a"].ToString(), RowKey = "own-order" });
        app.Orders.Visible.Add(new Order { CustomerId = app.AccountIds["customer-b"].ToString(), RowKey = "foreign-order" });
        using var customer = await SignedIn();
        var html = await customer.GetStringAsync("/Orders?customerId=" + app.AccountIds["customer-b"], TestContext.Current.CancellationToken);
        Assert.Contains("own-order", html);
        Assert.DoesNotContain("foreign-order", html);
        using var admin = await SignedIn("administrator");
        Assert.Contains("foreign-order", await admin.GetStringAsync("/Orders", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Azure_order_query_applies_customer_filter_and_admin_can_see_all()
    {
        var tables = new TestTables();
        tables.Table("Orders").Rows.Add(new Order { CustomerId = "12", RowKey = "owned" });
        tables.Table("Orders").Rows.Add(new Order { CustomerId = "13", RowKey = "foreign" });
        tables.Table("Orders").Rows.Add(new Order { PartitionKey = "Unexpected", CustomerId = "12" });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["AzureStorage:ConnectionString"] = "UseDevelopmentStorage=true" }).Build();
        var queue = new Azure.Storage.Queues.QueueClient("UseDevelopmentStorage=true", "operations",
            new Azure.Storage.Queues.QueueClientOptions { Transport = new NoNetworkTransport() });
        var orders = new AzureOrderStore(tables, config);
        Assert.Equal("owned", Assert.Single(await orders.ListAsync("12", false)).RowKey);
        Assert.Equal(2, (await orders.ListAsync("12", true)).Count);
    }

    [Fact]
    public async Task Cart_reads_and_deletes_are_scoped_to_account_ID_not_form_or_query_identity()
    {
        var cart = app.Tables.Table("Cart");
        cart.Rows.Clear();
        var own = new CartItem { PartitionKey = app.AccountIds["customer-a"].ToString(), RowKey = "own-cart", ProductName = "Owned product" };
        var foreign = new CartItem { PartitionKey = app.AccountIds["customer-b"].ToString(), RowKey = "foreign-cart", ProductName = "Foreign product" };
        cart.Rows.Add(own); cart.Rows.Add(foreign);
        using var client = await SignedIn();
        var html = await client.GetStringAsync("/Cart?customerId=" + foreign.PartitionKey, TestContext.Current.CancellationToken);
        Assert.Contains("Owned product", html);
        Assert.DoesNotContain("Foreign product", html);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "/Cart/Remove", new()
        { ["rowKey"] = foreign.RowKey, ["customerId"] = foreign.PartitionKey })).StatusCode);
        Assert.Contains(foreign, cart.Rows);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Cart/Remove", new() { ["rowKey"] = own.RowKey })).StatusCode);
        Assert.DoesNotContain(own, cart.Rows);
    }

    [Fact]
    public async Task Cart_estimates_use_current_catalog_prices_not_submitted_query_values()
    {
        var products = app.Tables.Table("Products");
        products.Rows.Clear();
        products.Rows.Add(new Product { RowKey = "estimate-product", Name = "Notebook", Price = "19.95" });
        var cart = app.Tables.Table("Cart");
        cart.Rows.Clear();
        cart.Rows.Add(new CartItem { PartitionKey = app.AccountIds["customer-a"].ToString(),
            RowKey = "estimate-cart", ProductId = "estimate-product", ProductName = "Notebook", Quantity = 2 });
        using var client = await SignedIn();
        var html = await client.GetStringAsync("/Cart?price=0.01&total=0.02", TestContext.Current.CancellationToken);
        Assert.Contains("R 39.90", html);
        Assert.Contains("Estimated total", html);
        Assert.Contains("/Cart/Checkout", html);
        Assert.DoesNotContain("R 0.02", html);
    }

    [Fact]
    public async Task Cart_with_unavailable_product_does_not_present_an_invented_zero_total()
    {
        app.Tables.Table("Products").Rows.Clear();
        var cart = app.Tables.Table("Cart");
        cart.Rows.Clear();
        cart.Rows.Add(new CartItem { PartitionKey = app.AccountIds["customer-a"].ToString(),
            RowKey = "missing-cart", ProductId = "missing-product", ProductName = "Unavailable product", Quantity = 1 });
        using var client = await SignedIn();
        var html = await client.GetStringAsync("/Cart", TestContext.Current.CancellationToken);
        Assert.Contains("Price unavailable", html);
        Assert.Contains("At checkout", html);
        Assert.DoesNotContain("R 0.00", html);
    }

    [Fact]
    public async Task Product_form_posts_to_existing_cart_action_and_owner_is_server_assigned()
    {
        app.Tables.Table("Products").Rows.Clear();
        app.Tables.Table("Products").Rows.Add(new Product { RowKey = "cart-test-product", Name = "Product" });
        using var client = await SignedIn();
        var html = await client.GetStringAsync("/Tables/Products", TestContext.Current.CancellationToken);
        Assert.Contains("/Cart/Add", html);
        Assert.DoesNotContain("/Cart/AddToCart", html);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(client, "/Cart/Add", new()
        { ["productId"] = "cart-test-product", ["customerId"] = "forged" })).StatusCode);
        var added = app.Tables.Table("Cart").Rows.OfType<CartItem>().Last();
        Assert.Equal(app.AccountIds["customer-a"].ToString(), added.PartitionKey);
    }

    [Fact]
    public async Task Changed_role_invalidates_an_existing_admin_cookie()
    {
        using var client = await SignedIn("changed-role");
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
            var user = (await users.FindByNameAsync("changed-role"))!;
            user.Role = AccessPolicies.CustomerRole;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Changed_security_stamp_invalidates_an_existing_cookie()
    {
        using var client = await SignedIn("changed-password");
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByNameAsync("changed-password"))!)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Orders", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Disabled_account_loses_access_with_an_existing_cookie()
    {
        using var client = await SignedIn("disabled-after-login");
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<LoginUser>>();
            var user = (await users.FindByNameAsync("disabled-after-login"))!;
            user.IsEnabled = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Redirect,
            (await client.GetAsync("/Customer", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Valid_administrator_can_process_an_order_with_antiforgery()
    {
        using var client = await SignedIn("administrator");
        var previous = app.Orders.ProcessCalls;
        Assert.Equal(HttpStatusCode.Redirect,
            (await Post(client, "/Orders/Process", new() { ["orderId"] = "order-to-process" })).StatusCode);
        Assert.Equal(previous + 1, app.Orders.ProcessCalls);
    }

    [Fact]
    public async Task Client_supplied_role_cannot_promote_a_customer_at_login()
    {
        using var client = app.Browser();
        var result = await Post(client, "/Login/Index", new()
        { ["username"] = "customer-a", ["password"] = app.Password, ["role"] = "Admin" });
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.StartsWith("/Customer", result.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/Admin", TestContext.Current.CancellationToken)).StatusCode);
    }
}
//-------------------End Of File------------------//

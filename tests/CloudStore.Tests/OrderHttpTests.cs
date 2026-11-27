//summary//
// Exercises real order controllers and workflow services behind the existing login/antiforgery middleware.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using System.Text.RegularExpressions;
using CloudStore.Orders;
using Xunit;
namespace CloudStore.Tests;
public sealed class OrderRetailFactory : RetailFactory
{
    private readonly OrderHarness _pipeline = new();
    public override OrderHarness Pipeline => _pipeline;
}
public class OrderHttpTests(OrderRetailFactory app) : IClassFixture<OrderRetailFactory>
{
    private async Task<HttpClient> Customer()
    {
        var client = app.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await app.Login(client, "customer-a")).StatusCode);
        return client;
    }
    private static async Task<HttpResponseMessage> Submit(HttpClient client, string requestId, string quantity = "1") =>
        await client.PostAsync("/Orders/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RequestId"] = requestId, ["ProductId"] = "product", ["Quantity"] = quantity,
            ["__RequestVerificationToken"] = await RetailFactory.Token(client)
        }), TestContext.Current.CancellationToken);
    [Fact]
    public async Task Order_form_supplies_a_request_reference_and_replay_creates_only_one_record()
    {
        using var client = await Customer();
        var html = await client.GetStringAsync("/Orders/Create", TestContext.Current.CancellationToken);
        var match = Regex.Match(html, "name=\"RequestId\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success); Assert.True(Guid.TryParse(match.Groups[1].Value, out _));
        var before = app.Pipeline.Store.Rows.Count;
        Assert.Equal(HttpStatusCode.Redirect, (await Submit(client, match.Groups[1].Value)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Submit(client, match.Groups[1].Value)).StatusCode);
        Assert.Equal(before + 1, app.Pipeline.Store.Rows.Count);
    }
    [Fact]
    public async Task Reusing_reference_for_different_input_returns_conflict()
    {
        using var client = await Customer(); var id = Guid.NewGuid().ToString();
        await Submit(client, id);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(client, id, "2")).StatusCode);
    }
    [Fact]
    public async Task Unconfirmed_insert_retains_the_form_reference_for_safe_retry()
    {
        using var client = await Customer(); var id = Guid.NewGuid().ToString();
        var before = app.Pipeline.Store.Rows.Count; app.Pipeline.Store.FailCreateBeforeCommit = true;
        var response = await Submit(client, id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(id, html); Assert.Contains("Retry this same form", html);
        Assert.Equal(before, app.Pipeline.Store.Rows.Count);
        Assert.Equal(HttpStatusCode.Redirect, (await Submit(client, id)).StatusCode);
        Assert.Equal(before + 1, app.Pipeline.Store.Rows.Count);
    }
    [Fact]
    public async Task Delayed_queue_send_keeps_order_visible_and_replay_does_not_duplicate_it()
    {
        using var client = await Customer(); var id = Guid.NewGuid().ToString();
        var before = app.Pipeline.Store.Rows.Count; app.Pipeline.Queue.Failures = 1;
        Assert.Equal(HttpStatusCode.Redirect, (await Submit(client, id)).StatusCode);
        Assert.Equal(before + 1, app.Pipeline.Store.Rows.Count);
        var html = await client.GetStringAsync("/Orders", TestContext.Current.CancellationToken);
        Assert.Contains("Delivery is delayed", html);
        Assert.Contains(OrderRules.ManualId(app.AccountIds["customer-a"].ToString(), id), html);
        Assert.Equal(HttpStatusCode.Redirect, (await Submit(client, id)).StatusCode);
        Assert.Equal(before + 1, app.Pipeline.Store.Rows.Count);
    }
    [Fact]
    public async Task Administrator_can_recover_and_complete_an_order_but_cannot_process_pending_directly()
    {
        using var customer = await Customer(); var nonce = Guid.NewGuid().ToString();
        app.Pipeline.Queue.Failures = 1; await Submit(customer, nonce);
        var id = OrderRules.ManualId(app.AccountIds["customer-a"].ToString(), nonce);
        using var admin = app.Browser(); await app.Login(admin, "administrator");
        async Task<HttpResponseMessage> Action(string action) => await admin.PostAsync("/Orders/" + action,
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["orderId"] = id, ["__RequestVerificationToken"] = await RetailFactory.Token(admin) }), TestContext.Current.CancellationToken);
        await Action("Process"); Assert.Equal("Pending", app.Pipeline.Store.Rows[id].Status);
        Assert.Equal(HttpStatusCode.Redirect, (await Action("RetryDelivery")).StatusCode);
        await app.Pipeline.Processor.ProcessAsync(new OrderMessage(1, id).Encode());
        Assert.Equal(HttpStatusCode.Redirect, (await Action("Process")).StatusCode);
        Assert.Equal("Processed", app.Pipeline.Store.Rows[id].Status);
    }
    [Fact]
    public async Task Recovery_limit_is_validated_on_the_HTTP_path()
    {
        using var admin = app.Browser(); await app.Login(admin, "administrator");
        var response = await admin.PostAsync("/Orders/RetryPending", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["limit"] = "21", ["__RequestVerificationToken"] = await RetailFactory.Token(admin) }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
//-------------------End Of File------------------//

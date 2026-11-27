//summary//
// Covers duplicate delivery, interrupted persistence/sends, ETag races and bounded order recovery.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Orders;
using Xunit;
namespace CloudStore.Tests;
public class OrderWorkflowTests
{
    [Fact]
    public async Task Save_precedes_send_and_server_prices_are_snapshotted()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request();
        request.UnitPriceCents = 1; request.TotalPriceCents = 1; request.Status = "Processed";
        app.Queue.AfterAcceptance = async () => Assert.NotNull(await app.Store.GetAsync(request.RowKey));
        var result = await app.Orders.SubmitAsync(request);
        var saved = app.Store.Rows[request.RowKey];
        Assert.True(result.Accepted); Assert.Equal(1250, saved.UnitPriceCents); Assert.Equal(2500, saved.TotalPriceCents);
        Assert.Equal("Pending", saved.Status); Assert.Equal("Sent", saved.QueueStatus);
        Assert.Equal(request.RowKey, Assert.Single(app.Queue.Messages).OrderId);
        Assert.Single(app.Activity.Saved);
    }
    [Fact]
    public async Task Same_request_is_persisted_once_and_preserves_original_price_on_retry()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request();
        await app.Orders.SubmitAsync(request); app.Products.Rows["product"].Price = "99.00";
        await app.Orders.SubmitAsync(request);
        Assert.Single(app.Store.Rows); Assert.Single(app.Queue.Messages); Assert.Equal(1250, app.Store.Rows[request.RowKey].UnitPriceCents);
    }
    [Fact]
    public async Task Request_key_reuse_with_a_different_quantity_is_rejected()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        request.Quantity++;
        await Assert.ThrowsAsync<OrderConflictException>(() => app.Orders.SubmitAsync(request));
        Assert.Single(app.Store.Rows);
    }
    [Fact]
    public void Owners_and_cart_manual_requests_have_distinct_stable_keys()
    {
        var nonce = Guid.NewGuid().ToString();
        Assert.Equal(OrderRules.ManualId("1", nonce), OrderRules.ManualId("1", nonce));
        Assert.NotEqual(OrderRules.ManualId("1", nonce), OrderRules.ManualId("2", nonce));
        Assert.NotEqual(OrderRules.CartId("1", nonce), OrderRules.ManualId("1", nonce));
    }
    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("1.001")][InlineData("12,50")][InlineData("invalid")][InlineData("1000000.01")]
    public async Task Invalid_product_prices_never_create_or_enqueue(string price)
    {
        var app = new OrderHarness(); app.Products.Rows["product"].Price = price;
        await Assert.ThrowsAsync<OrderValidationException>(() => app.Orders.SubmitAsync(OrderHarness.Request()));
        Assert.Empty(app.Store.Rows); Assert.Empty(app.Queue.Messages);
    }
    [Fact]
    public async Task Missing_product_never_creates_or_enqueues()
    {
        var app = new OrderHarness(); app.Products.Rows.Clear();
        await Assert.ThrowsAsync<OrderValidationException>(() => app.Orders.SubmitAsync(OrderHarness.Request()));
        Assert.Empty(app.Store.Rows); Assert.Empty(app.Queue.Messages);
    }
    [Fact]
    public async Task Failed_persistence_never_sends_and_same_request_can_be_retried()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); app.Store.FailCreateBeforeCommit = true;
        await Assert.ThrowsAsync<RequestFailedException>(() => app.Orders.SubmitAsync(request));
        Assert.Empty(app.Queue.Messages);
        await app.Orders.SubmitAsync(request); Assert.Single(app.Store.Rows);
    }
    [Fact]
    public async Task Committed_insert_with_lost_response_is_read_back_without_duplicate()
    {
        var app = new OrderHarness(); app.Store.FailCreateAfterCommit = true; var request = OrderHarness.Request();
        Assert.True((await app.Orders.SubmitAsync(request)).Accepted);
        await app.Orders.SubmitAsync(request); Assert.Single(app.Store.Rows); Assert.Single(app.Queue.Messages);
    }
    [Fact]
    public async Task Queue_failure_keeps_saved_order_and_stale_recovery_sends_it()
    {
        var app = new OrderHarness(); app.Queue.Failures = 1; var request = OrderHarness.Request();
        Assert.False((await app.Orders.SubmitAsync(request)).Accepted);
        Assert.Single(app.Store.Rows); Assert.Equal("Pending", app.Store.Rows[request.RowKey].QueueStatus);
        Assert.Equal(0, (await app.Dispatcher.RetryPendingAsync(20)).Examined);
        app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await app.Dispatcher.RetryPendingAsync(20)).Accepted);
        Assert.Single(app.Store.Rows); Assert.Single(app.Queue.Messages);
    }
    [Fact]
    public async Task Lost_queue_acknowledgement_may_redeliver_but_processing_is_idempotent()
    {
        var app = new OrderHarness(); app.Queue.AcceptThenFail = true; var request = OrderHarness.Request();
        Assert.False((await app.Orders.SubmitAsync(request)).Accepted);
        Assert.True((await app.Orders.SubmitAsync(request)).Accepted);
        Assert.Equal(2, app.Queue.Messages.Count);
        foreach (var message in app.Queue.Messages) await app.Processor.ProcessAsync(message.Encode());
        Assert.Single(app.Store.Rows); Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Repeated_and_concurrent_deliveries_do_not_insert_again()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        var body = app.Queue.Messages.Single().Encode();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.Processor.ProcessAsync(body)));
        Assert.Single(app.Store.Rows); Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Duplicate_delivery_after_admin_processing_preserves_terminal_state_and_time()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        var body = app.Queue.Messages.Single().Encode(); await app.Processor.ProcessAsync(body);
        await app.Orders.ProcessAsync(request.RowKey); var processed = app.Store.Rows[request.RowKey].ProcessedAtUtc;
        app.Clock.Advance(TimeSpan.FromHours(1)); await app.Processor.ProcessAsync(body); await app.Orders.ProcessAsync(request.RowKey);
        Assert.Equal("Processed", app.Store.Rows[request.RowKey].Status);
        Assert.Equal(processed, app.Store.Rows[request.RowKey].ProcessedAtUtc);
    }
    [Fact]
    public async Task Admin_cannot_skip_queue_processing()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        await Assert.ThrowsAsync<OrderValidationException>(() => app.Orders.ProcessAsync(request.RowKey));
        Assert.Equal("Pending", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Worker_retries_an_ETag_conflict_and_preserves_price()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        app.Store.PatchConflicts = 1; await app.Processor.ProcessAsync(app.Queue.Messages.Single().Encode());
        Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status); Assert.Equal(2500, app.Store.Rows[request.RowKey].TotalPriceCents);
    }
    [Fact]
    public async Task Worker_conflict_retry_does_not_downgrade_a_concurrently_processed_row()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        app.Store.BeforePatch = (order, patch) => { if (patch.Status == "Ready") { order.Status = "Processed"; order.ETag = new ETag("changed"); } };
        await app.Processor.ProcessAsync(app.Queue.Messages.Single().Encode());
        Assert.Equal("Processed", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Exhausted_conflicts_propagate_for_host_retry()
    {
        var app = new OrderHarness(); await app.Orders.SubmitAsync(OrderHarness.Request()); app.Store.PatchConflicts = 10;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Processor.ProcessAsync(app.Queue.Messages.Single().Encode()));
        Assert.Equal("Pending", app.Store.Rows.Values.Single().Status);
    }
    [Theory]
    [InlineData("")][InlineData("not JSON")][InlineData("null")][InlineData("{}")] [InlineData("{\"version\":2,\"orderId\":\"wrong\"}")]
    public async Task Malformed_messages_are_rejected_instead_of_acknowledged(string body)
    {
        var app = new OrderHarness(); await Assert.ThrowsAsync<InvalidDataException>(() => app.Processor.ProcessAsync(body));
        Assert.Empty(app.Store.Rows);
    }
    [Fact]
    public async Task Missing_order_is_not_recreated_from_a_message()
    {
        var app = new OrderHarness(); var body = new OrderMessage(1, OrderHarness.Request().RowKey).Encode();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.Processor.ProcessAsync(body)); Assert.Empty(app.Store.Rows);
    }
    [Fact]
    public async Task Processing_before_sender_receipt_does_not_restore_pending_state()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request();
        app.Queue.AfterAcceptance = () => app.Processor.ProcessAsync(app.Queue.Messages.Last().Encode());
        Assert.True((await app.Orders.SubmitAsync(request)).Accepted); Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Automatic_attempts_stop_at_five_and_explicit_admin_retry_is_one_attempt()
    {
        var app = new OrderHarness(); app.Queue.Failures = 10; var request = OrderHarness.Request();
        await app.Orders.SubmitAsync(request);
        for (var i = 0; i < 8; i++) { app.Clock.Advance(TimeSpan.FromMinutes(6)); await app.Dispatcher.RetryPendingAsync(20); }
        Assert.Equal(5, app.Queue.Attempts); Assert.Equal("Failed", app.Store.Rows[request.RowKey].QueueStatus);
        app.Queue.Failures = 0; Assert.True((await app.Dispatcher.RetryAsync(request.RowKey)).Accepted);
        Assert.Equal(6, app.Queue.Attempts);
    }
    [Fact]
    public async Task Recent_sent_orders_are_not_resent_by_recovery_but_stale_ones_are()
    {
        var app = new OrderHarness(); await app.Orders.SubmitAsync(OrderHarness.Request());
        Assert.Equal(0, (await app.Dispatcher.RetryPendingAsync(20)).Examined);
        app.Clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(1, (await app.Dispatcher.RetryPendingAsync(20)).Accepted);
        Assert.Equal(2, app.Queue.Messages.Count); Assert.Single(app.Store.Rows);
    }
    [Fact]
    public async Task Recovery_is_bounded_to_twenty_records()
    {
        var app = new OrderHarness();
        for (var i = 0; i < 25; i++) await app.Orders.SubmitAsync(OrderHarness.Request());
        app.Clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(20, (await app.Dispatcher.RetryPendingAsync(20)).Examined);
        await Assert.ThrowsAsync<OrderValidationException>(() => app.Dispatcher.RetryPendingAsync(21));
        Assert.Equal(45, app.Queue.Attempts);
    }
    [Fact]
    public async Task Partial_checkout_retains_delayed_items_and_retry_does_not_duplicate_saved_orders()
    {
        var app = new OrderHarness(); app.CartLine("first"); app.CartLine("second"); app.Queue.Failures = 1;
        var first = await app.Checkout.CompleteAsync("1"); Assert.Equal(1, first.Completed); Assert.Equal(1, first.Awaiting);
        Assert.Equal(2, app.Store.Rows.Count); Assert.Single(app.Cart.Rows);
        var second = await app.Checkout.CompleteAsync("1"); Assert.Equal(1, second.Completed);
        Assert.Equal(2, app.Store.Rows.Count); Assert.Empty(app.Cart.Rows);
    }
    [Fact]
    public async Task Lost_cart_cleanup_can_be_retried_without_another_order()
    {
        var app = new OrderHarness(); app.CartLine("line"); app.Cart.FailRemoval = true;
        Assert.Equal(1, (await app.Checkout.CompleteAsync("1")).Failed); Assert.Single(app.Cart.Rows);
        Assert.Equal(1, (await app.Checkout.CompleteAsync("1")).Completed);
        Assert.Single(app.Store.Rows); Assert.Empty(app.Cart.Rows); Assert.Single(app.Queue.Messages);
    }
    [Fact]
    public async Task New_or_foreign_cart_rows_are_not_cleared_by_checkout()
    {
        var app = new OrderHarness(); app.CartLine("line"); app.CartLine("foreign", "2");
        app.Cart.BeforeRemoval = () => { app.Cart.BeforeRemoval = null; app.CartLine("new-line"); };
        await app.Checkout.CompleteAsync("1");
        Assert.Equal(2, app.Cart.Rows.Count); Assert.Contains(app.Cart.Rows, row => row.RowKey == "new-line");
        Assert.Contains(app.Cart.Rows, row => row.PartitionKey == "2"); Assert.Single(app.Store.Rows);
    }
    [Fact]
    public async Task Changed_cart_row_is_not_deleted_with_a_stale_ETag()
    {
        var app = new OrderHarness(); app.CartLine("line");
        app.Cart.BeforeRemoval = () => app.Cart.Rows.Single().ETag = new ETag("new-version");
        Assert.Equal(1, (await app.Checkout.CompleteAsync("1")).Failed); Assert.Single(app.Cart.Rows); Assert.Single(app.Store.Rows);
    }
    [Fact]
    public async Task Concurrent_checkouts_share_the_same_order_key()
    {
        var app = new OrderHarness(); app.CartLine("line");
        await Task.WhenAll(app.Checkout.CompleteAsync("1"), app.Checkout.CompleteAsync("1"));
        Assert.Single(app.Store.Rows); Assert.Empty(app.Cart.Rows);
    }
    [Theory]
    [InlineData(0)][InlineData(-1)][InlineData(1001)]
    public async Task Service_rejects_invalid_quantities_before_saving(int quantity)
    {
        var app = new OrderHarness();
        await Assert.ThrowsAsync<OrderValidationException>(() => app.Orders.SubmitAsync(OrderHarness.Request(quantity: quantity)));
        Assert.Empty(app.Store.Rows); Assert.Empty(app.Queue.Messages);
    }
    [Fact]
    public async Task Permanent_delivery_error_requires_explicit_admin_action()
    {
        var app = new OrderHarness(); app.Queue.FailureStatus = 403; app.Queue.Failures = 1;
        var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        Assert.Equal("Failed", app.Store.Rows[request.RowKey].QueueStatus);
        app.Clock.Advance(TimeSpan.FromMinutes(6)); Assert.Equal(0, (await app.Dispatcher.RetryPendingAsync(20)).Examined);
        Assert.True((await app.Dispatcher.RetryAsync(request.RowKey)).Accepted); Assert.Single(app.Store.Rows);
    }
    [Fact]
    public async Task Unrecorded_send_receipt_retains_recoverable_order()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request();
        app.Store.BeforePatch = (_, patch) =>
        {
            if (patch.QueueStatus == "Sent")
            {
                app.Store.BeforePatch = null;
                throw new RequestFailedException(503, "Test receipt write failure.");
            }
        };
        Assert.False((await app.Orders.SubmitAsync(request)).Accepted);
        Assert.Equal("Pending", app.Store.Rows[request.RowKey].QueueStatus);
        Assert.True((await app.Orders.SubmitAsync(request)).Accepted);
        foreach (var message in app.Queue.Messages) await app.Processor.ProcessAsync(message.Encode());
        Assert.Single(app.Store.Rows); Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status);
    }
    [Fact]
    public async Task Older_failed_sender_cannot_overwrite_newer_successful_receipt()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); app.Queue.AcceptThenFail = true;
        app.Queue.AfterAcceptance = () =>
        {
            var saved = app.Store.Rows[request.RowKey]; saved.DispatchAttempts = 2;
            saved.QueueStatus = "Sent"; saved.ETag = new ETag("newer-receipt");
            return Task.CompletedTask;
        };
        Assert.True((await app.Orders.SubmitAsync(request)).Accepted);
        Assert.Equal("Sent", app.Store.Rows[request.RowKey].QueueStatus);
        Assert.Equal(2, app.Store.Rows[request.RowKey].DispatchAttempts);
    }
    [Fact]
    public async Task Corrupt_candidate_is_flagged_for_investigation_instead_of_blocking_every_batch()
    {
        var app = new OrderHarness(); var bad = OrderHarness.Request(); var good = OrderHarness.Request();
        await app.Orders.SubmitAsync(bad); await app.Orders.SubmitAsync(good);
        app.Store.Rows[bad.RowKey].UnitPriceCents = -1; app.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, (await app.Dispatcher.RetryPendingAsync(1)).Examined);
        Assert.Equal("Failed", app.Store.Rows[bad.RowKey].QueueStatus);
        Assert.Equal(1, (await app.Dispatcher.RetryPendingAsync(1)).Accepted);
    }
    [Fact]
    public async Task Storage_failure_propagates_for_queue_host_retry_without_state_change()
    {
        var app = new OrderHarness(); var request = OrderHarness.Request(); await app.Orders.SubmitAsync(request);
        app.Store.ReadFailures = 1; var body = app.Queue.Messages.Single().Encode();
        await Assert.ThrowsAsync<RequestFailedException>(() => app.Processor.ProcessAsync(body));
        Assert.Equal("Pending", app.Store.Rows[request.RowKey].Status);
        await app.Processor.ProcessAsync(body); Assert.Equal("Ready", app.Store.Rows[request.RowKey].Status);
    }
    [Theory]
    [InlineData(0)][InlineData(2)]
    public async Task Unsupported_message_versions_are_rejected_even_with_a_valid_order_ID(int version)
    {
        var app = new OrderHarness();
        await Assert.ThrowsAsync<InvalidDataException>(() => app.Processor.ProcessAsync(
            new OrderMessage(version, OrderHarness.Request().RowKey).Encode()));
        Assert.Empty(app.Store.Rows);
    }
    [Fact]
    public void Queue_contract_contains_no_customer_or_product_snapshot()
    {
        using var document = System.Text.Json.JsonDocument.Parse(new OrderMessage(1, OrderHarness.Request().RowKey).Encode());
        Assert.Equal(new[] { "orderId", "version" }, document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
    }
    [Fact]
    public async Task Azure_delivery_patch_uses_the_expected_ETag_and_merge_without_overwriting_status_or_price()
    {
        var tables = new TestTables();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var store = new AzureOrderStore(tables, config); var expected = new ETag("real-version");
        await store.PatchAsync(OrderHarness.Request().RowKey, expected, new OrderPatch(QueueStatus: "Sent"));
        var table = tables.Table("Orders"); Assert.Equal(expected, table.LastPatchETag);
        Assert.Equal(Azure.Data.Tables.TableUpdateMode.Merge, table.LastPatchMode);
        Assert.Equal("Sent", table.LastPatch!["QueueStatus"]);
        Assert.False(table.LastPatch.ContainsKey("Status")); Assert.False(table.LastPatch.ContainsKey("UnitPriceCents"));
    }
}
//-------------------End Of File------------------//

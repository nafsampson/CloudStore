//summary//
// Records delivery attempts on the order row; retries are bounded and never alter the processing state.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Microsoft.Extensions.Logging;
using CloudStore.Monitoring;
namespace CloudStore.Orders;
public sealed class OrderDispatcher(IOrderStore store, IOrderQueue queue, TimeProvider clock,
    ILogger<OrderDispatcher> logger) : IOrderRecovery
{
    public async Task<DispatchResult> DispatchAsync(string id, bool explicitRetry = false)
    {
        using var activity = CloudStoreTelemetry.Activities.StartActivity("order.dispatch");
        for (var conflict = 0; conflict < 3; conflict++)
        {
            var order = await store.GetAsync(id);
            if (order is null) return new(false, "NotFound");
            if (order.Status is OrderRules.Ready or OrderRules.Processed) return new(true, order.Status);
            if (order.Status != OrderRules.Pending) return new(false, order.Status);
            try { OrderRules.CheckSnapshot(order); }
            catch (InvalidDataException)
            {
                try { await store.PatchAsync(id, order.ETag, new OrderPatch(QueueStatus: OrderRules.Failed)); }
                catch (RequestFailedException error) when (error.Status == 412) { continue; }
                return new(false, OrderRules.Failed);
            }
            if (!explicitRetry)
            {
                if (order.QueueStatus == OrderRules.Sent && order.LastDispatchAttemptUtc > clock.GetUtcNow().AddMinutes(-5))
                    return new(true, OrderRules.Sent);
                if (order.DispatchAttempts >= OrderRules.MaximumAttempts || order.QueueStatus == OrderRules.Failed)
                    return new(false, OrderRules.Failed);
            }
            try
            {
                await store.PatchAsync(id, order.ETag, new OrderPatch(QueueStatus: OrderRules.Pending,
                    DispatchAttempts: checked(order.DispatchAttempts + 1), LastDispatchAttemptUtc: clock.GetUtcNow()));
            }
            catch (RequestFailedException error) when (error.Status == 412) { continue; }

            try { await queue.SendAsync(new OrderMessage(1, id)); }
            catch (Exception error) when (OrderRules.IsTransient(error) || error is RequestFailedException)
            {
                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "Delivery unconfirmed");
                CloudStoreTelemetry.Processing.Add(1, new KeyValuePair<string, object?>("outcome", "unconfirmed"));
                logger.LogWarning("Delivery of order {OrderId} was not confirmed; recovery remains available.", id);
                return await MarkDeliveryAsync(id, sent: false, permanent: !OrderRules.IsTransient(error), order.DispatchAttempts + 1);
            }
            CloudStoreTelemetry.Processing.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
            return await MarkDeliveryAsync(id, sent: true, permanent: false, order.DispatchAttempts + 1);
        }
        return new(false, "Busy");
    }

    private async Task<DispatchResult> MarkDeliveryAsync(string id, bool sent, bool permanent, int attemptNumber)
    {
        for (var conflict = 0; conflict < 3; conflict++)
        {
            try
            {
                var latest = await store.GetAsync(id);
                if (latest is null) return new(false, "NotFound");
                // A worker/admin may have advanced the row before the sender records its receipt.
                if (latest.Status is OrderRules.Ready or OrderRules.Processed) return new(true, latest.Status);
                if (latest.Status != OrderRules.Pending) return new(false, latest.Status);
                // An older sender must not overwrite the receipt of a newer attempt.
                if (latest.DispatchAttempts != attemptNumber)
                    return new(sent || latest.QueueStatus == OrderRules.Sent, latest.QueueStatus);
                var state = sent ? OrderRules.Sent
                    : permanent || latest.DispatchAttempts >= OrderRules.MaximumAttempts ? OrderRules.Failed : OrderRules.Pending;
                await store.PatchAsync(id, latest.ETag, new OrderPatch(QueueStatus: state));
                return new(sent, state);
            }
            catch (RequestFailedException error) when (error.Status == 412) { }
            catch (Exception error) when (OrderRules.IsTransient(error))
            {
                logger.LogWarning("The delivery receipt for order {OrderId} could not be saved.", id);
                return new(false, OrderRules.Pending);
            }
        }
        return new(false, "Busy");
    }

    public Task<DispatchResult> RetryAsync(string id)
    {
        if (!OrderRules.IsId(id)) throw new OrderValidationException("The order identifier is invalid.");
        // An administrator explicitly approves one additional attempt, even after automatic attempts stop.
        return DispatchAsync(id, explicitRetry: true);
    }
    public async Task<RecoveryResult> RetryPendingAsync(int limit)
    {
        if (limit is < 1 or > 20) throw new OrderValidationException("Retry between 1 and 20 orders at a time.");
        var candidates = await store.RecoveryCandidatesAsync(clock.GetUtcNow().AddMinutes(-5), limit);
        var accepted = 0;
        foreach (var candidate in candidates)
        {
            try { if ((await DispatchAsync(candidate.RowKey)).Accepted) accepted++; }
            catch (Exception error) when (OrderRules.IsTransient(error) || error is RequestFailedException or InvalidDataException)
            { logger.LogWarning("Order {OrderId} could not be recovered in this batch.", candidate.RowKey); }
        }
        return new(candidates.Count, accepted, candidates.Count - accepted);
    }
}
//-------------------End Of File------------------//

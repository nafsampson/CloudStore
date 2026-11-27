//summary//
// Advances a saved Pending order once; duplicates and ETag conflicts never recreate or downgrade an order.
//summary//
//-------------------Start Of File------------------//
using Azure;
namespace CloudStore.Orders;
public sealed class OrderProcessor(IOrderStore store, TimeProvider clock)
{
    public async Task ProcessAsync(string body)
    {
        var message = OrderMessage.Parse(body);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var order = await store.GetAsync(message.OrderId)
                ?? throw new InvalidOperationException("The saved order is not available; retry delivery.");
            if (order.Status is OrderRules.Ready or OrderRules.Processed) return;
            if (order.Status != OrderRules.Pending) throw new InvalidDataException("The order state cannot be processed.");
            OrderRules.CheckSnapshot(order);
            try
            {
                await store.PatchAsync(order.RowKey, order.ETag,
                    new OrderPatch(Status: OrderRules.Ready, QueueStatus: OrderRules.Sent, ReadyAtUtc: clock.GetUtcNow()));
                return;
            }
            catch (RequestFailedException error) when (error.Status == 412) { }
        }
        throw new InvalidOperationException("The order changed during processing; retry delivery.");
    }
}
//-------------------End Of File------------------//

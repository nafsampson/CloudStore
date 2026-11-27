//summary//
// Persists validated orders once before delivery; retries preserve original pricing and ownership.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Models;
using CloudStore.Orders;
namespace CloudStore.Services;
public class OrderAccess(IOrderStore store, IProductLookup products, OrderDispatcher dispatcher,
    TimeProvider clock, IOrderActivity activity) : IOrderAccess
{
    public Task<List<Order>> ListAsync(string accountId, bool administrator) => store.ListAsync(accountId, administrator);
    public async Task<SubmissionResult> SubmitAsync(Order request)
    {
        if (!OrderRules.IsId(request.RowKey) || !OrderRules.IsOwner(request.CustomerId)
            || string.IsNullOrWhiteSpace(request.ProductId) || request.Quantity is < 1 or > 1000)
            throw new OrderValidationException("Choose a product and a quantity between 1 and 1000.");
        var saved = await store.GetAsync(request.RowKey);
        if (saved is null)
        {
            var product = await products.GetAsync(request.ProductId)
                ?? throw new OrderValidationException("This product is no longer available. Your cart has been kept.");
            var cents = OrderRules.PriceCents(product.Price);
            var snapshot = new Order
            {
                RowKey = request.RowKey, CustomerId = request.CustomerId, ProductId = request.ProductId,
                ProductName = product.Name, Quantity = request.Quantity, UnitPriceCents = cents,
                TotalPriceCents = checked(cents * request.Quantity), OrderDate = clock.GetUtcNow().UtcDateTime
            };
            try { await store.CreateAsync(snapshot); saved = snapshot; await activity.SavedAsync(snapshot.RowKey); }
            catch (Exception error) when (OrderRules.IsTransient(error) || error is RequestFailedException { Status: 409 })
            {
                // A timed-out insert may have committed. Read the stable key before reporting failure.
                saved = await store.GetAsync(request.RowKey);
                if (saved is null) throw;
            }
        }
        if (saved.CustomerId != request.CustomerId || saved.ProductId != request.ProductId || saved.Quantity != request.Quantity)
            throw new OrderConflictException();
        OrderRules.CheckSnapshot(saved);
        var result = await dispatcher.DispatchAsync(saved.RowKey);
        return new(saved, result.Accepted);
    }
    public async Task<bool> ProcessAsync(string orderId)
    {
        if (!OrderRules.IsId(orderId)) throw new OrderValidationException("The order identifier is invalid.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var order = await store.GetAsync(orderId);
            if (order is null) return false;
            if (order.Status == OrderRules.Processed) return true;
            if (order.Status != OrderRules.Ready)
                throw new OrderValidationException("The order must finish queue processing before it can be marked as processed.");
            try
            {
                await store.PatchAsync(orderId, order.ETag,
                    new OrderPatch(Status: OrderRules.Processed, ProcessedAtUtc: clock.GetUtcNow()));
                return true;
            }
            catch (RequestFailedException error) when (error.Status == 412) { }
        }
        throw new OrderValidationException("The order changed. Refresh the list and try again.");
    }
}
//-------------------End Of File------------------//

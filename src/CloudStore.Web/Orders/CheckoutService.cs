//summary//
// Completes cart lines independently so retries cannot duplicate orders or remove new/changed cart items.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Models;
using CloudStore.Services;
using Microsoft.Extensions.Logging;
namespace CloudStore.Orders;
public sealed record CheckoutResult(int Completed, int Awaiting, int Failed);
public sealed class CheckoutService(ICartStore cart, IOrderAccess orders, ILogger<CheckoutService> logger)
{
    public async Task<CheckoutResult> CompleteAsync(string accountId)
    {
        var completed = 0; var awaiting = 0; var failed = 0;
        // Only the captured row/version is removed. Items added during checkout remain in the cart.
        foreach (var item in await cart.ReadAsync(accountId))
        {
            try
            {
                if (item.PartitionKey != accountId) throw new OrderValidationException("Invalid cart ownership.");
                var result = await orders.SubmitAsync(new Order
                {
                    RowKey = OrderRules.CartId(accountId, item.RowKey), CustomerId = accountId,
                    ProductId = item.ProductId, Quantity = item.Quantity
                });
                if (!result.Accepted) { awaiting++; continue; }
                await cart.RemoveAsync(accountId, item.RowKey, item.ETag);
                completed++;
            }
            catch (Exception error) when (OrderRules.IsTransient(error)
                || error is RequestFailedException or OrderValidationException or OrderConflictException)
            {
                failed++;
                logger.LogWarning("A checkout line for account {AccountId} remains available for retry.", accountId);
            }
        }
        return new(completed, awaiting, failed);
    }
}
//-------------------End Of File------------------//

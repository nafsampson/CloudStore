//summary//
// Defines atomic order creation and ETag-checked patches shared by the web app and worker.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Models;
namespace CloudStore.Orders;
public interface IOrderStore
{
    Task<Order?> GetAsync(string id);
    Task CreateAsync(Order order);
    Task PatchAsync(string id, ETag expected, OrderPatch patch);
    Task<List<Order>> ListAsync(string accountId, bool administrator);
    Task<List<Order>> RecoveryCandidatesAsync(DateTimeOffset before, int limit);
}
public interface IOrderQueue { Task SendAsync(OrderMessage message); }
public interface IOrderActivity { Task SavedAsync(string id); }
public interface IProductLookup { Task<Product?> GetAsync(string id); }
public interface ICartStore
{
    Task<List<CartItem>> ReadAsync(string accountId);
    Task RemoveAsync(string accountId, string rowKey, ETag expected);
}
public interface IOrderRecovery
{
    Task<DispatchResult> RetryAsync(string id);
    Task<RecoveryResult> RetryPendingAsync(int limit);
}
//-------------------End Of File------------------//

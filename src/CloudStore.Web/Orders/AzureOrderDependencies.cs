//summary//
// Adapts product reads, plain JSON queue sends and ETag-checked cart cleanup to the Azure SDK.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using CloudStore.Models;
using Microsoft.Extensions.Logging;
namespace CloudStore.Orders;
public sealed class AzureProductLookup(TableServiceClient tables, IConfiguration config) : IProductLookup
{
    public async Task<Product?> GetAsync(string id)
    {
        var result = await tables.GetTableClient(config["AzureStorage:ProductTable"] ?? "Products")
            .GetEntityIfExistsAsync<Product>("Product", id);
        return result.HasValue ? result.Value : null;
    }
}
public sealed class AzureOrderQueue : IOrderQueue
{
    private readonly QueueClient _queue;
    public AzureOrderQueue(IConfiguration config)
    {
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(5);
        _queue = new QueueClient(config["AzureStorage:ConnectionString"],
            config["AzureStorage:OrderQueueName"] ?? "orders-queue", options);
    }
    public async Task SendAsync(OrderMessage message) => await _queue.SendMessageAsync(message.Encode());
}
public sealed class AzureOrderActivity(QueueClient operations, ILogger<AzureOrderActivity> logger) : IOrderActivity
{
    public async Task SavedAsync(string id)
    {
        try { await operations.SendMessageAsync($"Order saved: {id}"); }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is RequestFailedException)
        { logger.LogWarning("The optional operations notification for order {OrderId} was not delivered.", id); }
    }
}
public sealed class AzureCartStore(TableServiceClient tables, IConfiguration config) : ICartStore
{
    private readonly TableClient _cart = tables.GetTableClient(config["AzureStorage:CartTable"] ?? "Cart");
    public async Task<List<CartItem>> ReadAsync(string accountId)
    {
        var result = new List<CartItem>();
        await foreach (var item in _cart.QueryAsync<CartItem>(item => item.PartitionKey == accountId)) result.Add(item);
        return result;
    }
    public async Task RemoveAsync(string accountId, string rowKey, ETag expected)
    {
        try { await _cart.DeleteEntityAsync(accountId, rowKey, expected); }
        catch (RequestFailedException error) when (error.Status == 404) { }
    }
}
//-------------------End Of File------------------//

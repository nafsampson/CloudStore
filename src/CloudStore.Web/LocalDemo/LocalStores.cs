//summary//
// Implements the existing application storage boundaries over durable local SQLite records.
//summary//
//-------------------Start Of File------------------//
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using CloudStore.Models;
using CloudStore.Orders;
using CloudStore.Services;
using CloudStore.Uploads;

namespace CloudStore.LocalDemo;

public sealed class LocalRetailTables(DemoDatabase database) : IRetailTables
{
    public IRetailTable GetTable(string name)
    {
        if (name is not ("Customers" or "Products" or "Orders" or "Cart")) throw new InvalidOperationException("Local demo uses the documented default table names.");
        return new LocalTable(database, name);
    }
    private sealed class LocalTable(DemoDatabase database, string name) : IRetailTable
    {
        public IEnumerable<T> Query<T>(Expression<Func<T, bool>> filter) where T : class, ITableEntity, new() => database.Read<T>(name).Where(filter.Compile());
        public Task AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, ITableEntity
        { cancellationToken.ThrowIfCancellationRequested(); database.Add(name, entity); return Task.CompletedTask; }
        public Task UpdateEntityAsync<T>(T entity, ETag expected, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) where T : class, ITableEntity
        { cancellationToken.ThrowIfCancellationRequested(); database.Update(name, entity, expected, mode); return Task.CompletedTask; }
        public Task DeleteEntityAsync(string partition, string id, ETag expected = default, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); database.Delete(name, partition, id, expected); return Task.CompletedTask; }
    }
}
public sealed class LocalOrderStore(DemoDatabase database) : IOrderStore
{
    public Task<Order?> GetAsync(string id) => Task.FromResult(database.Read<Order>("Orders").SingleOrDefault(order => order.PartitionKey == "Order" && order.RowKey == id));
    public Task CreateAsync(Order order) { database.Add("Orders", order); return Task.CompletedTask; }
    public Task PatchAsync(string id, ETag expected, OrderPatch patch)
    {
        var entity = new TableEntity("Order", id);
        if (patch.Status is not null) entity[nameof(Order.Status)] = patch.Status;
        if (patch.QueueStatus is not null) entity[nameof(Order.QueueStatus)] = patch.QueueStatus;
        if (patch.DispatchAttempts is not null) entity[nameof(Order.DispatchAttempts)] = patch.DispatchAttempts.Value;
        if (patch.LastDispatchAttemptUtc is not null) entity[nameof(Order.LastDispatchAttemptUtc)] = patch.LastDispatchAttemptUtc.Value;
        if (patch.ReadyAtUtc is not null) entity[nameof(Order.ReadyAtUtc)] = patch.ReadyAtUtc.Value;
        if (patch.ProcessedAtUtc is not null) entity[nameof(Order.ProcessedAtUtc)] = patch.ProcessedAtUtc.Value;
        database.Update("Orders", entity, expected, TableUpdateMode.Merge); return Task.CompletedTask;
    }
    public Task<List<Order>> ListAsync(string accountId, bool administrator) => Task.FromResult(database.Read<Order>("Orders")
        .Where(order => order.PartitionKey == "Order" && (administrator || order.CustomerId == accountId)).ToList());
    public Task<List<Order>> RecoveryCandidatesAsync(DateTimeOffset before, int limit) => Task.FromResult(database.Read<Order>("Orders")
        .Where(order => order.PartitionKey == "Order" && order.Status == OrderRules.Pending && order.QueueStatus != OrderRules.Failed && order.DispatchAttempts < OrderRules.MaximumAttempts && order.LastDispatchAttemptUtc <= before).Take(limit).ToList());
}
public sealed class LocalProductLookup(DemoDatabase database) : IProductLookup
{
    public Task<Product?> GetAsync(string id) => Task.FromResult(database.Read<Product>("Products").SingleOrDefault(product => product.PartitionKey == "Product" && product.RowKey == id));
}
public sealed class LocalCartStore(DemoDatabase database) : ICartStore
{
    public Task<List<CartItem>> ReadAsync(string accountId) => Task.FromResult(database.Read<CartItem>("Cart").Where(item => item.PartitionKey == accountId).ToList());
    public Task RemoveAsync(string accountId, string rowKey, ETag expected)
    {
        // Missing rows are already removed; a changed row must remain for reconciliation.
        try { database.Delete("Cart", accountId, rowKey, expected); }
        catch (RequestFailedException exception) when (exception.Status == 404) { }
        return Task.CompletedTask;
    }
}
public sealed class LocalOrderQueue(DemoDatabase database) : IOrderQueue
{
    public Task SendAsync(OrderMessage message) { database.Enqueue("orders", message.Encode()); return Task.CompletedTask; }
}
public sealed class LocalActivity(DemoDatabase database) : IOperationsLog, IOrderActivity, IUploadEvents
{
    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) throw new ArgumentException("Invalid operations message.");
        database.Enqueue("operations", text); return Task.CompletedTask;
    }
    public Task<List<string>> PeekAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.PeekOperations());
    public Task SavedAsync(string id) => SendAsync("Order saved: " + id);
    public Task RecordedAsync(string name, CancellationToken cancellationToken = default) => SendAsync("Upload saved: " + name, cancellationToken);
}
//-------------------End Of File------------------//

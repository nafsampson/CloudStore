//summary//
// Models durable records with copied reads, atomic inserts and ETag conflicts for deterministic failure tests.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Models;
using CloudStore.Orders;
using CloudStore.Services;
using Microsoft.Extensions.Logging.Abstractions;
namespace CloudStore.Tests;

public sealed class MemoryOrders : IOrderStore
{
    public Dictionary<string, Order> Rows { get; } = new();
    private int _version;
    public bool FailCreateBeforeCommit, FailCreateAfterCommit;
    public int PatchConflicts, PatchCalls;
    public int ReadFailures;
    public Action<Order, OrderPatch>? BeforePatch;
    private readonly object _gate = new();
    public static T Copy<T>(T value) where T : new()
    {
        var result = new T();
        foreach (var property in typeof(T).GetProperties().Where(property => property.CanRead && property.CanWrite))
            property.SetValue(result, property.GetValue(value));
        return result;
    }
    public async Task<Order?> GetAsync(string id)
    {
        await Task.Yield();
        if (ReadFailures > 0) { ReadFailures--; throw new RequestFailedException(503, "Test read unavailable."); }
        lock (_gate) return Rows.TryGetValue(id, out var row) ? Copy(row) : null;
    }
    public Task CreateAsync(Order order)
    {
        lock (_gate)
        {
            if (FailCreateBeforeCommit) { FailCreateBeforeCommit = false; throw new RequestFailedException(503, "Test unavailable."); }
            if (Rows.ContainsKey(order.RowKey)) throw new RequestFailedException(409, "Test conflict.");
            var row = Copy(order); row.ETag = new ETag((++_version).ToString()); Rows.Add(row.RowKey, row);
            if (FailCreateAfterCommit) { FailCreateAfterCommit = false; throw new RequestFailedException(503, "Test lost insert response."); }
            return Task.CompletedTask;
        }
    }
    public Task PatchAsync(string id, ETag expected, OrderPatch patch)
    {
        lock (_gate)
        {
            PatchCalls++;
            var row = Rows[id];
            BeforePatch?.Invoke(row, patch);
            if (PatchConflicts > 0) { PatchConflicts--; throw new RequestFailedException(412, "Test changed."); }
            if (row.ETag != expected) throw new RequestFailedException(412, "Test stale version.");
            foreach (var property in typeof(OrderPatch).GetProperties())
                if (property.GetValue(patch) is { } value) typeof(Order).GetProperty(property.Name)!.SetValue(row, value);
            row.ETag = new ETag((++_version).ToString());
            return Task.CompletedTask;
        }
    }
    public Task<List<Order>> ListAsync(string accountId, bool administrator) => Task.FromResult(
        Rows.Values.Where(order => administrator || order.CustomerId == accountId).Select(Copy).ToList());
    public Task<List<Order>> RecoveryCandidatesAsync(DateTimeOffset before, int limit) => Task.FromResult(Rows.Values
        .Where(order => order.Status == OrderRules.Pending && order.QueueStatus != OrderRules.Failed
            && order.DispatchAttempts < OrderRules.MaximumAttempts && order.LastDispatchAttemptUtc <= before)
        .Take(limit).Select(Copy).ToList());
}
public sealed class MemoryQueue : IOrderQueue
{
    public List<OrderMessage> Messages { get; } = new();
    public int Attempts, Failures;
    public int FailureStatus = 503;
    public bool AcceptThenFail;
    public Func<Task>? AfterAcceptance;
    public async Task SendAsync(OrderMessage message)
    {
        Interlocked.Increment(ref Attempts);
        if (Failures-- > 0) throw new RequestFailedException(FailureStatus, "Test queue failure.");
        lock (Messages) Messages.Add(message);
        if (AfterAcceptance is not null) await AfterAcceptance();
        if (AcceptThenFail) { AcceptThenFail = false; throw new RequestFailedException(503, "Test lost send response."); }
    }
}
public sealed class MemoryProducts : IProductLookup
{
    public Dictionary<string, Product> Rows { get; } = new();
    public Task<Product?> GetAsync(string id) => Task.FromResult(Rows.GetValueOrDefault(id));
}
public sealed class MemoryActivity : IOrderActivity
{
    public List<string> Saved { get; } = new();
    public Task SavedAsync(string id) { Saved.Add(id); return Task.CompletedTask; }
}
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan span) => _now += span;
}
public sealed class MemoryCart : ICartStore
{
    private readonly object _gate = new();
    public List<CartItem> Rows { get; } = new();
    public bool FailRemoval;
    public Action? BeforeRemoval;
    public Task<List<CartItem>> ReadAsync(string accountId)
    {
        lock (_gate) return Task.FromResult(Rows.Where(item => item.PartitionKey == accountId).Select(MemoryOrders.Copy).ToList());
    }
    public Task RemoveAsync(string accountId, string rowKey, ETag expected)
    {
        lock (_gate)
        {
        BeforeRemoval?.Invoke();
        if (FailRemoval) { FailRemoval = false; throw new RequestFailedException(503, "Test cleanup failure."); }
        var row = Rows.FirstOrDefault(item => item.PartitionKey == accountId && item.RowKey == rowKey);
        if (row is null) return Task.CompletedTask;
        if (row.ETag != expected) throw new RequestFailedException(412, "Test cart changed.");
        Rows.Remove(row); return Task.CompletedTask;
        }
    }
}
public sealed class OrderHarness
{
    public MemoryOrders Store { get; } = new();
    public MemoryQueue Queue { get; } = new();
    public MemoryProducts Products { get; } = new();
    public MemoryCart Cart { get; } = new();
    public MemoryActivity Activity { get; } = new();
    public TestClock Clock { get; } = new();
    public OrderDispatcher Dispatcher { get; }
    public OrderAccess Orders { get; }
    public OrderProcessor Processor { get; }
    public CheckoutService Checkout { get; }
    public OrderHarness()
    {
        Products.Rows["product"] = new Product { RowKey = "product", Name = "Test product", Price = "12.50" };
        Dispatcher = new(Store, Queue, Clock, NullLogger<OrderDispatcher>.Instance);
        Orders = new(Store, Products, Dispatcher, Clock, Activity);
        Processor = new(Store, Clock);
        Checkout = new(Cart, Orders, NullLogger<CheckoutService>.Instance);
    }
    public static Order Request(string? request = null, string owner = "1", int quantity = 2) => new()
    {
        RowKey = OrderRules.ManualId(owner, request ?? Guid.NewGuid().ToString()),
        CustomerId = owner, ProductId = "product", Quantity = quantity
    };
    public void CartLine(string id, string owner = "1") => Cart.Rows.Add(new CartItem
    { PartitionKey = owner, RowKey = id, ProductId = "product", Quantity = 1, ETag = new ETag("cart-v1") });
}
//-------------------End Of File------------------//

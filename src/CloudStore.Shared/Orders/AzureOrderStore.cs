//summary//
// Uses atomic inserts and merge patches with real ETags; never upserts or replaces a later order state.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;
using CloudStore.Models;
using Microsoft.Extensions.Configuration;
using System.Linq.Expressions;
namespace CloudStore.Orders;
public sealed class AzureOrderStore : IOrderStore
{
    private readonly TableClient _table;
    public AzureOrderStore(TableServiceClient tables, IConfiguration config) =>
        _table = tables.GetTableClient(config["OrderTable"] ?? config["AzureStorage:OrderTable"] ?? "Orders");
    public async Task<Order?> GetAsync(string id)
    {
        var response = await _table.GetEntityIfExistsAsync<Order>("Order", id);
        return response.HasValue ? response.Value : null;
    }
    public async Task CreateAsync(Order order) => await _table.AddEntityAsync(order);
    public async Task PatchAsync(string id, ETag expected, OrderPatch patch)
    {
        var entity = new TableEntity("Order", id);
        if (patch.Status is not null) entity[nameof(Order.Status)] = patch.Status;
        if (patch.QueueStatus is not null) entity[nameof(Order.QueueStatus)] = patch.QueueStatus;
        if (patch.DispatchAttempts is not null) entity[nameof(Order.DispatchAttempts)] = patch.DispatchAttempts.Value;
        if (patch.LastDispatchAttemptUtc is not null) entity[nameof(Order.LastDispatchAttemptUtc)] = patch.LastDispatchAttemptUtc.Value;
        if (patch.ReadyAtUtc is not null) entity[nameof(Order.ReadyAtUtc)] = patch.ReadyAtUtc.Value;
        if (patch.ProcessedAtUtc is not null) entity[nameof(Order.ProcessedAtUtc)] = patch.ProcessedAtUtc.Value;
        await _table.UpdateEntityAsync(entity, expected, TableUpdateMode.Merge);
    }
    public async Task<List<Order>> ListAsync(string accountId, bool administrator)
    {
        Expression<Func<Order, bool>> filter = administrator ? order => order.PartitionKey == "Order"
            : order => order.PartitionKey == "Order" && order.CustomerId == accountId;
        var result = new List<Order>();
        await foreach (var order in _table.QueryAsync(filter)) result.Add(order);
        return result;
    }
    public async Task<List<Order>> RecoveryCandidatesAsync(DateTimeOffset before, int limit)
    {
        var result = new List<Order>();
        await foreach (var order in _table.QueryAsync<Order>(order => order.PartitionKey == "Order"
            && order.Status == OrderRules.Pending && order.QueueStatus != OrderRules.Failed
            && order.DispatchAttempts < OrderRules.MaximumAttempts
            && order.LastDispatchAttemptUtc <= before, maxPerPage: limit))
        {
            result.Add(order);
            if (result.Count == limit) break;
        }
        return result;
    }
}
//-------------------End Of File------------------//

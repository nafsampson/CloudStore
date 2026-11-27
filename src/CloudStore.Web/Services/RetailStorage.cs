//summary//
// Storage boundaries used by the same MVC pages in Azure and local demo mode.
//summary//
//-------------------Start Of File------------------//
using System.Linq.Expressions;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
namespace CloudStore.Services;

public interface IRetailTables { IRetailTable GetTable(string name); }
public interface IRetailTable
{
    IEnumerable<T> Query<T>(Expression<Func<T, bool>> filter) where T : class, ITableEntity, new();
    Task AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, ITableEntity;
    Task UpdateEntityAsync<T>(T entity, ETag expected, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) where T : class, ITableEntity;
    Task DeleteEntityAsync(string partition, string id, ETag expected = default, CancellationToken cancellationToken = default);
}
public sealed class AzureRetailTables(TableServiceClient client) : IRetailTables
{
    public IRetailTable GetTable(string name) => new AzureRetailTable(client.GetTableClient(name));
    private sealed class AzureRetailTable(TableClient table) : IRetailTable
    {
        public IEnumerable<T> Query<T>(Expression<Func<T, bool>> filter) where T : class, ITableEntity, new() => table.Query(filter);
        public async Task AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, ITableEntity => await table.AddEntityAsync(entity, cancellationToken);
        public async Task UpdateEntityAsync<T>(T entity, ETag expected, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) where T : class, ITableEntity => await table.UpdateEntityAsync(entity, expected, mode, cancellationToken);
        public async Task DeleteEntityAsync(string partition, string id, ETag expected = default, CancellationToken cancellationToken = default) => await table.DeleteEntityAsync(partition, id, expected, cancellationToken);
    }
}
public interface IOperationsLog
{
    Task SendAsync(string text, CancellationToken cancellationToken = default);
    Task<List<string>> PeekAsync(CancellationToken cancellationToken = default);
}
public sealed class AzureOperationsLog(QueueClient queue) : IOperationsLog
{
    public async Task SendAsync(string text, CancellationToken cancellationToken = default) => await queue.SendMessageAsync(text, cancellationToken);
    public async Task<List<string>> PeekAsync(CancellationToken cancellationToken = default) =>
        (await queue.PeekMessagesAsync(32, cancellationToken)).Value.Select(message => message.MessageText).Reverse().Take(10).ToList();
}
//-------------------End Of File------------------//

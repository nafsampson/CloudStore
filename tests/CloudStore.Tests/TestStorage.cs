//summary//
// Implements only the storage operations exercised by tests; unsupported network operations fail locally.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using System.Linq.Expressions;

namespace CloudStore.Tests;

public sealed class TestTables : TableServiceClient
{
    public Dictionary<string, TestTable> Tables { get; } = new();
    public override TableClient GetTableClient(string tableName)
    {
        if (!Tables.TryGetValue(tableName, out var table)) Tables[tableName] = table = new TestTable();
        return table;
    }
    public TestTable Table(string name) => (TestTable)GetTableClient(name);
}

public sealed class TestTable : TableClient
{
    public List<ITableEntity> Rows { get; } = new();
    public TableEntity? LastPatch { get; private set; }
    public ETag LastPatchETag { get; private set; }
    public TableUpdateMode LastPatchMode { get; private set; }
    public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge,
        CancellationToken cancellationToken = default)
    {
        LastPatch = entity as TableEntity;
        LastPatchETag = ifMatch;
        LastPatchMode = mode;
        return Task.FromResult<Response>(new TestResponse(204));
    }
    public override Pageable<T> Query<T>(Expression<Func<T, bool>> filter, int? maxPerPage = null,
        IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var matches = Rows.OfType<T>().Where(filter.Compile()).ToArray();
        return Pageable<T>.FromPages([Page<T>.FromValues(matches, null, new TestResponse(200))]);
    }
    public override AsyncPageable<T> QueryAsync<T>(Expression<Func<T, bool>> filter, int? maxPerPage = null,
        IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var matches = Rows.OfType<T>().Where(filter.Compile()).ToArray();
        return AsyncPageable<T>.FromPages([Page<T>.FromValues(matches, null, new TestResponse(200))]);
    }
    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        Rows.Add(entity);
        return Task.FromResult<Response>(new TestResponse(204));
    }
    public override Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch = default,
        CancellationToken cancellationToken = default)
    {
        var entity = Rows.FirstOrDefault(row => row.PartitionKey == partitionKey && row.RowKey == rowKey);
        if (entity is null) throw new RequestFailedException(404, "Entity not found.");
        Rows.Remove(entity);
        return Task.FromResult<Response>(new TestResponse(204));
    }
}

public sealed class NoNetworkTransport : HttpPipelineTransport
{
    public override Request CreateRequest() => HttpClientTransport.Shared.CreateRequest();
    public override void Process(HttpMessage message) => throw new InvalidOperationException("Network access is forbidden in tests.");
    public override ValueTask ProcessAsync(HttpMessage message) => throw new InvalidOperationException("Network access is forbidden in tests.");
}

public sealed class TestResponse(int status) : Response
{
    public override int Status => status;
    public override string ReasonPhrase => "Test response";
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = "test";
    public override void Dispose() { }
    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
}
//-------------------End Of File------------------//

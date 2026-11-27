//summary//
// Defines saved order snapshots, lifecycle state and delivery bookkeeping.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;
namespace CloudStore.Models;
public class Order : ITableEntity
{
    public string PartitionKey { get; set; } = "Order";
    public string RowKey { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public long UnitPriceCents { get; set; }
    public long TotalPriceCents { get; set; }
    public DateTime OrderDate { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending";
    public string QueueStatus { get; set; } = "Pending";
    public int DispatchAttempts { get; set; }
    public DateTimeOffset LastDispatchAttemptUtc { get; set; } = DateTimeOffset.UnixEpoch;
    public DateTimeOffset? ReadyAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public ETag ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
}
//-------------------End Of File------------------//

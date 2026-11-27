//summary//
// Defines account-owned cart rows and their product and quantity information.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;

namespace CloudStore.Models
{
    public class CartItem : ITableEntity
    {
        public string PartitionKey { get; set; } = "";  // CustomerId
        public string RowKey { get; set; } = "";        // CartItemId

        public string ProductId { get; set; } = "";
        public string ProductName { get; set; } = "";
        public int Quantity { get; set; }

        public ETag ETag { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }
}
//-------------------End Of File------------------//

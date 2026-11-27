//summary//
// Defines product catalog records, pricing and the associated private image reference.
//summary//
//-------------------Start Of File------------------//
using Azure.Data.Tables;
using Azure;

namespace CloudStore.Models
{
    public class Product : ITableEntity
    {
        public string PartitionKey { get; set; } = "Product";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Sku { get; set; } = "";
        public string Price { get; set; } = "";

        
        public string? ImageName { get; set; }
        public string? ImageUrl { get; set; }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}
//-------------------End Of File------------------//

//summary//
// Defines customer directory records and their Azure Table identity and version fields.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;

namespace CloudStore.Models
{


    public class Customer : ITableEntity
    {
        public string PartitionKey { get; set; } = "Customer";
        public string RowKey { get; set; } = Guid.NewGuid().ToString();
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }

}
//-------------------End Of File------------------//

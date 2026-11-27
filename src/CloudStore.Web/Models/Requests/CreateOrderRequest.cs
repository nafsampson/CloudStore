//summary//
// Accepts product and quantity only; ownership, status and order keys are assigned by the server.
//summary//
//-------------------Start Of File------------------//
using System.ComponentModel.DataAnnotations;
namespace CloudStore.Models.Requests;
public class CreateOrderRequest
{
    [Required, RegularExpression("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    public string RequestId { get; set; } = "";
    [Required, StringLength(128)]
    public string ProductId { get; set; } = "";
    [Range(1, 1000)]
    public int Quantity { get; set; } = 1;
}
//-------------------End Of File------------------//

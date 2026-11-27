//summary//
// Separates account-scoped order access from MVC so permissions can be tested without Azure.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Models;
using CloudStore.Orders;
namespace CloudStore.Services;
public interface IOrderAccess
{
    Task<List<Order>> ListAsync(string accountId, bool administrator);
    Task<SubmissionResult> SubmitAsync(Order order);
    Task<bool> ProcessAsync(string orderId);
}
//-------------------End Of File------------------//

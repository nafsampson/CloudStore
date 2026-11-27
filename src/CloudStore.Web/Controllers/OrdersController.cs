//summary//
// Shows account-scoped orders; customers submit their own orders and only administrators process them.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CloudStore.Models;
using CloudStore.Models.Requests;
using CloudStore.Security;
using CloudStore.Services;
using CloudStore.Orders;
namespace CloudStore.Controllers;

[Authorize(Policy = AccessPolicies.RetailUser)]
public class OrdersController(IOrderAccess orders, IOrderRecovery recovery) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index() => View(await orders.ListAsync(
        AccessPolicies.AccountId(User), User.IsInRole(AccessPolicies.AdminRole)));

    [HttpGet, Authorize(Policy = AccessPolicies.Customer)]
    public IActionResult Create() => View(new CreateOrderRequest { RequestId = Guid.NewGuid().ToString("D") });

    [HttpPost, Authorize(Policy = AccessPolicies.Customer)]
    public async Task<IActionResult> Create(CreateOrderRequest request)
    {
        if (!ModelState.IsValid) return View(request);
        try
        {
            var accountId = AccessPolicies.AccountId(User);
            var result = await orders.SubmitAsync(new Order
            {
                RowKey = OrderRules.ManualId(accountId, request.RequestId), CustomerId = accountId,
                ProductId = request.ProductId, Quantity = request.Quantity
            });
            TempData["Message"] = result.Accepted ? "Your order is saved and awaiting processing."
                : "Your order is saved. Delivery is delayed; an administrator can retry it without creating another order.";
            return RedirectToAction(nameof(Index));
        }
        catch (OrderValidationException error) { ModelState.AddModelError("", error.Message); }
        catch (OrderConflictException error) { Response.StatusCode = 409; ModelState.AddModelError("", error.Message); }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is Azure.RequestFailedException)
        { ModelState.AddModelError("", "We could not confirm your order. Retry this same form; its request reference prevents duplicates."); }
        return View(request);
    }

    [HttpPost, Authorize(Policy = AccessPolicies.Administrator)]
    public async Task<IActionResult> Process(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) return BadRequest();
        try { if (!await orders.ProcessAsync(orderId)) return NotFound(); }
        catch (OrderValidationException error) { TempData["Message"] = error.Message; return RedirectToAction(nameof(Index)); }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is Azure.RequestFailedException)
        {
            TempData["Message"] = "We could not confirm processing. Refresh the list before retrying; the saved order is kept.";
            return RedirectToAction(nameof(Index));
        }
        TempData["Message"] = "Order marked as processed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = AccessPolicies.Administrator)]
    public async Task<IActionResult> RetryDelivery(string orderId)
    {
        try
        {
            var result = await recovery.RetryAsync(orderId);
            if (result.State == "NotFound") return NotFound();
            TempData["Message"] = result.Accepted ? "Order delivery is confirmed or processing has already completed."
                : "The order is still saved. Delivery could not be confirmed; check the service before retrying.";
        }
        catch (OrderValidationException error) { return BadRequest(error.Message); }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is Azure.RequestFailedException)
        { TempData["Message"] = "Delivery could not be confirmed. The saved order has been kept."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = AccessPolicies.Administrator)]
    public async Task<IActionResult> RetryPending(int limit = 20)
    {
        try
        {
            var result = await recovery.RetryPendingAsync(limit);
            TempData["Message"] = $"Checked {result.Examined} delayed orders: {result.Accepted} deliveries confirmed; {result.Awaiting} still awaiting recovery.";
        }
        catch (OrderValidationException error) { return BadRequest(error.Message); }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is Azure.RequestFailedException)
        { TempData["Message"] = "Delayed orders could not be checked. No saved orders were removed."; }
        return RedirectToAction(nameof(Index));
    }
}
//-------------------End Of File------------------//

//summary//
// Handles account-owned cart reads, edits and checkout through the shared order workflow.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Services;
// Keeps cart ownership server-side and delegates checkout to the shared retry-safe order workflow.
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Azure;
using Azure.Data.Tables;
using CloudStore.Security;
using CloudStore.Models;
using CloudStore.Orders;
namespace CloudStore.Controllers;

[Authorize(Policy = AccessPolicies.Customer)]
public class CartController : Controller
{
    private readonly IRetailTable _products;
    private readonly IRetailTable _cart;
    private readonly CheckoutService _checkout;
    public CartController(IConfiguration config, IRetailTables tables, CheckoutService checkout)
    {
        _products = tables.GetTable(config["AzureStorage:ProductTable"] ?? "Products");
        _cart = tables.GetTable(config["AzureStorage:CartTable"] ?? "Cart");
        _checkout = checkout;
    }
    private string AccountId => AccessPolicies.AccountId(User);
    [HttpGet]
    public IActionResult Index()
    {
        var items = _cart.Query<CartItem>(item => item.PartitionKey == AccountId).ToList();
        var selected = items.Select(item => item.ProductId).ToHashSet(StringComparer.Ordinal);
        var prices = new Dictionary<string, long>(StringComparer.Ordinal);
        if (items.Count > 0)
        {
            foreach (var product in _products.Query<Product>(product => product.PartitionKey == "Product"))
            {
                if (!selected.Contains(product.RowKey)) continue;
                try { prices[product.RowKey] = OrderRules.PriceCents(product.Price); }
                catch (OrderValidationException) { /* Unavailable pricing is explicit in the view. */ }
            }
        }
        return View(new CartPage(items.Select(item => new CartLine(item.RowKey, item.ProductId,
            item.ProductName, item.Quantity, prices.TryGetValue(item.ProductId, out var price) ? price : null)).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Add(string productId)
    {
        var product = _products.Query<Product>(item => item.PartitionKey == "Product" && item.RowKey == productId).FirstOrDefault();
        if (product is null) return NotFound();
        await _cart.AddEntityAsync(new CartItem
        {
            PartitionKey = AccountId, RowKey = Guid.NewGuid().ToString(),
            ProductId = productId, ProductName = product.Name, Quantity = 1
        });
        return RedirectToAction(nameof(Index));
    }
    [HttpPost]
    public async Task<IActionResult> Remove(string rowKey)
    {
        try { await _cart.DeleteEntityAsync(AccountId, rowKey); }
        catch (RequestFailedException error) when (error.Status == 404) { return NotFound(); }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost]
    public async Task<IActionResult> Checkout()
    {
        try
        {
            var result = await _checkout.CompleteAsync(AccountId);
            TempData["Message"] = result.Awaiting == 0 && result.Failed == 0
                ? $"Checkout completed for {result.Completed} items. Saved orders are awaiting processing."
                : $"Completed {result.Completed} items; {result.Awaiting} await delivery and {result.Failed} need another attempt. Your remaining cart is kept. Retry checkout safely without duplicating saved orders.";
        }
        catch (Exception error) when (OrderRules.IsTransient(error) || error is RequestFailedException)
        { TempData["Message"] = "We could not finish checking your cart. Your remaining items are kept; retry checkout safely."; }
        return RedirectToAction("Index", "Orders");
    }
}
//-------------------End Of File------------------//

//summary//
// Handles product management and authenticated product images through storage abstractions.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authorization;
using CloudStore.Security;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using CloudStore.Models;
using CloudStore.Services;
using Microsoft.AspNetCore.Mvc;
using CloudStore.Uploads;

namespace CloudStore.Controllers;


// Manages Products stored in Azure Table Storage
// Supports CRUD operations and integrates with Blob Storage
// and Queue Storage
[Authorize(Policy = AccessPolicies.RetailUser)]
public class TablesController : Controller
{
    private readonly IRetailTable _products;        // Product table
    private readonly IImageStore _images;
    private readonly IUploadEvents _uploads;

    public TablesController(IRetailTables tsc, IConfiguration cfg, IImageStore images, IUploadEvents uploads)
    {
        _products = tsc.GetTable(cfg["AzureStorage:ProductTable"] ?? "Products");
        _images = images;
        _uploads = uploads;
    }

    [Authorize(Policy = AccessPolicies.Administrator)]

    public IActionResult Index() => RedirectToAction(nameof(Products));

    // ----------------- Products -----------------

    // Lists all products
    public IActionResult Products() =>
        View(_products.Query<Product>(p => p.PartitionKey == "Product").ToList());


    // Shows product details by ID
    public IActionResult ProductDetails(string id)
    {
        var product = _products.Query<Product>(p => p.PartitionKey == "Product" && p.RowKey == id).FirstOrDefault();
        if (product == null) return NotFound();
        return View(product);
    }


    // Adds a product with optional image upload to Blob Storage
    // Sends queue notification when an image is uploaded
    [HttpPost]
    [Authorize(Policy = AccessPolicies.Administrator)]
    [RequestSizeLimit(UploadPolicy.ImageLimit + 65536), RequestFormLimits(MultipartBodyLengthLimit = UploadPolicy.ImageLimit + 65536)]
    public async Task<IActionResult> AddProduct(Product model, IFormFile? imageFile)
    {
        if (!ValidProduct(model)) return BadRequest("Provide a name, SKU and valid positive price.");
        model.PartitionKey = "Product";
        model.RowKey = Guid.NewGuid().ToString();
        // Ignore submitted URLs/names: an image must come through the validated upload path.
        model.ImageName = null;
        model.ImageUrl = null;
        try
        {
            if (imageFile is not null) model.ImageName = await SaveImage(imageFile);
            await _products.AddEntityAsync(model, HttpContext.RequestAborted);
            if (model.ImageName is not null) await _uploads.RecordedAsync(model.ImageName, HttpContext.RequestAborted);
        }
        catch (UploadRejected exception) { TempData["Error"] = exception.Message; }
        catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
        { TempData["Error"] = "Product save could not be confirmed. Check the product list before trying again."; }
        return RedirectToAction(nameof(Products));
    }

    // PRODUCT DELETE & UPDATE
    [HttpPost]
    [Authorize(Policy = AccessPolicies.Administrator)]
    public async Task<IActionResult> DeleteProduct(string rowKey)
    {
        await _products.DeleteEntityAsync("Product", rowKey);
        return RedirectToAction(nameof(Products));
    }

    [Authorize(Policy = AccessPolicies.Administrator)]

    public IActionResult EditProduct(string rowKey)
    {
        var product = _products.Query<Product>(p => p.PartitionKey == "Product" && p.RowKey == rowKey).FirstOrDefault();
        return product is null ? NotFound() : View(product);
    }

    [HttpPost]
    [Authorize(Policy = AccessPolicies.Administrator)]
    [RequestSizeLimit(UploadPolicy.ImageLimit + 65536), RequestFormLimits(MultipartBodyLengthLimit = UploadPolicy.ImageLimit + 65536)]
    public async Task<IActionResult> EditProduct(Product model, IFormFile? imageFile, string? version)
    {
        if (!ValidProduct(model)) return BadRequest("Provide a name, SKU and valid positive price.");
        var existing = _products.Query<Product>(p => p.PartitionKey == "Product" && p.RowKey == model.RowKey).FirstOrDefault();
        if (existing is null) return NotFound();
        if (string.IsNullOrEmpty(version) || version != existing.ETag.ToString())
            return Conflict("The product changed or its version is missing. Reload the edit page before saving.");
        // Merge only editable fields using the captured version; never trust image URL form fields.
        var patch = new TableEntity("Product", existing.RowKey)
        {
            ["Name"] = model.Name, ["Sku"] = model.Sku, ["Price"] = model.Price, ["ImageUrl"] = ""
        };
        try
        {
            if (imageFile is not null) patch["ImageName"] = await SaveImage(imageFile);
            await _products.UpdateEntityAsync(patch, existing.ETag, TableUpdateMode.Merge, HttpContext.RequestAborted);
            if (patch.TryGetValue("ImageName", out var name)) await _uploads.RecordedAsync((string)name, HttpContext.RequestAborted);
        }
        catch (UploadRejected exception) { TempData["Error"] = exception.Message; }
        catch (RequestFailedException exception) when (exception.Status == 412)
        { TempData["Error"] = "The product changed while saving. Reload it before editing again."; }
        catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
        { TempData["Error"] = "Product save could not be confirmed. Reload the product before trying again."; }
        return RedirectToAction(nameof(Products));
    }

    [HttpGet]
    public async Task<IActionResult> Image(string id, CancellationToken cancellationToken)
    {
        var product = _products.Query<Product>(p => p.PartitionKey == "Product" && p.RowKey == id).FirstOrDefault();
        if (product is null || !UploadPolicy.StoredName(product.ImageName, true)) return NotFound();
        var download = await _images.ReadAsync(product.ImageName!, cancellationToken);
        if (download is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        return File(download.Content, download.ContentType);
    }

    private async Task<string> SaveImage(IFormFile file)
    {
        if (file.Length <= 0 || file.Length > UploadPolicy.ImageLimit) throw new UploadRejected("Select a PNG or JPEG image up to 4 MiB.");
        await using var stream = file.OpenReadStream();
        var bytes = await UploadPolicy.ReadAsync(stream, UploadPolicy.ImageLimit, HttpContext.RequestAborted);
        if (bytes.Length != file.Length) throw new UploadRejected("The uploaded file length is invalid.");
        var upload = UploadPolicy.Validate(file.FileName, file.ContentType, bytes, true);
        await _images.SaveAsync(upload, HttpContext.RequestAborted);
        return upload.Name;
    }

    private static bool ValidProduct(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.Name) || product.Name.Length > 200 ||
            string.IsNullOrWhiteSpace(product.Sku) || product.Sku.Length > 100) return false;
        try { _ = Orders.OrderRules.PriceCents(product.Price); return true; }
        catch (Orders.OrderValidationException) { return false; }
    }
}
//-------------------End Of File------------------//

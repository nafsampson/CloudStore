//summary//
// Validates bounded product/customer requests for the explicitly enabled operator storage endpoint.
//summary//
//-------------------Start Of File------------------//
using Microsoft.Extensions.Configuration;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Azure.Data.Tables;
using CloudStore.Models; 
using System.Text.Json;
using CloudStore.Uploads;

public class TableWriteFunction
{
    private readonly TableServiceClient _tableService;
    private readonly IConfiguration _configuration;

    public TableWriteFunction(TableServiceClient tableService, IConfiguration configuration)
    {
        _tableService = tableService;
        _configuration = configuration;
    }

    [Function("TableWrite")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        if (!bool.TryParse(_configuration["EnableStorageHttpWrites"], out var enabled) || !enabled)
            return req.CreateResponse(HttpStatusCode.ServiceUnavailable);
        byte[] requestBody;
        JsonDocument doc;
        try
        {
            requestBody = await UploadPolicy.ReadAsync(req.Body, 65536);
            doc = JsonDocument.Parse(requestBody, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (Exception exception) when (exception is JsonException or UploadRejected)
        { return req.CreateResponse(HttpStatusCode.BadRequest); }
        using var document = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return req.CreateResponse(HttpStatusCode.BadRequest);
        if (!doc.RootElement.TryGetProperty("type", out var typeEl))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Missing 'type' in body");
            return bad;
        }

        if (typeEl.ValueKind != JsonValueKind.String) return req.CreateResponse(HttpStatusCode.BadRequest);
        var type = typeEl.GetString()?.ToLowerInvariant();
        if (type == "customer")
        {
            var tbl = _tableService.GetTableClient(_configuration["CustomerTable"] ?? "Customers");

            Customer? customer;
            try { customer = JsonSerializer.Deserialize<Customer>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }
            if (customer == null) return req.CreateResponse(HttpStatusCode.BadRequest);

            customer.PartitionKey = "Customer";
            customer.RowKey = Guid.NewGuid().ToString();
            await tbl.AddEntityAsync(customer);
            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteStringAsync("Customer saved");
            return resp;
        }
        else if (type == "product")
        {
            var tbl = _tableService.GetTableClient(_configuration["ProductTable"] ?? "Products");

            Product? product;
            try { product = JsonSerializer.Deserialize<Product>(requestBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
            catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }
            if (product == null) return req.CreateResponse(HttpStatusCode.BadRequest);

            product.PartitionKey = "Product";
            product.RowKey = Guid.NewGuid().ToString();
            // Table imports cannot publish caller-controlled image links or storage names.
            product.ImageName = null;
            product.ImageUrl = null;
            await tbl.AddEntityAsync(product);
            var resp = req.CreateResponse(HttpStatusCode.OK);
            await resp.WriteStringAsync("Product saved");
            return resp;
        }

        var r = req.CreateResponse(HttpStatusCode.BadRequest);
        await r.WriteStringAsync("Type must be customer or product");
        return r;
    }
}
//-------------------End Of File------------------//

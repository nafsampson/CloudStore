//summary//
// Seeds fictional electronics products and safely refreshes an existing local demo catalog.
// Preserves edited products, accounts and legacy items referenced by carts or historical orders.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Models;
using CloudStore.Uploads;
using Azure.Data.Tables;
namespace CloudStore.LocalDemo;

public static class DemoCatalog
{
    public static async Task<int> AddMissingAsync(DemoSettings settings, CancellationToken cancellationToken = default)
    {
        var db = new DemoDatabase(settings);
        var existing = db.Read<Product>("Products").ToDictionary(product => product.RowKey, StringComparer.Ordinal);
        var uploads = new LocalUploads(settings);
        var directory = new DirectoryInfo(settings.Directory);
        while (directory.Name != ".local-demo" && directory.Parent is not null) directory = directory.Parent;
        if (directory.Name != ".local-demo" || directory.Parent is null) throw new InvalidOperationException("Invalid local catalog directory.");
        var sampleRoot = Path.GetFullPath(Path.Combine(directory.Parent.FullName, "..", "..", "sample-data", "catalog"));
        var added = 0;
        foreach (var (id, name, sku, price) in new[] {
            ("demo-headphones", "Wireless Over-Ear Headphones", "AUD-001", "899.00"),
            ("demo-keyboard", "Compact Mechanical Keyboard", "KEY-001", "749.00"),
            ("demo-mouse", "Wireless Ergonomic Mouse", "MSE-001", "349.00"),
            ("demo-speaker", "Portable Bluetooth Speaker", "SPK-001", "599.00"),
            ("demo-webcam", "Desktop Webcam", "CAM-001", "649.00"),
            ("demo-hub", "USB-C Multiport Hub", "HUB-001", "499.00") })
        {
            existing.TryGetValue(id, out var product);
            if (product is not null && !string.IsNullOrEmpty(product.ImageName)) continue;
            // Local setup imports private uploads from offline sample data; Azure never publishes these source files.
            var bytes = await File.ReadAllBytesAsync(Path.Combine(sampleRoot, "electronics-" + id[5..] + ".png"), cancellationToken);
            var image = UploadPolicy.Validate(id[5..] + ".png", "image/png", bytes, true);
            await uploads.SaveAsync(image, cancellationToken);
            if (product is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                db.Update("Products", new TableEntity(product.PartitionKey, product.RowKey) { ["ImageName"] = image.Name }, product.ETag, TableUpdateMode.Merge);
                continue;
            }
            db.Add("Products", new Product { RowKey = id, Name = name, Sku = sku, Price = price, ImageName = image.Name });
            added++;
        }
        return added;
    }

    public static async Task<int> RefreshAsync(DemoSettings settings, CancellationToken cancellationToken = default)
    {
        settings.RequireReady();
        var added = await AddMissingAsync(settings, cancellationToken);
        var db = new DemoDatabase(settings);
        var referenced = db.Read<CartItem>("Cart").Select(item => item.ProductId)
            .Concat(db.Read<Order>("Orders").Select(order => order.ProductId)).ToHashSet(StringComparer.Ordinal);
        foreach (var product in db.Read<Product>("Products"))
        {
            var untouched = (product.RowKey, product.Name, product.Price, product.Sku) switch {
                ("demo-coffee", "Sample coffee beans", "129.95", "DEMO-COFFEE") => true,
                ("demo-tote", "Sample canvas tote", "89.50", "DEMO-TOTE") => true,
                ("demo-notebook", "Sample notebook", "45.00", "DEMO-NOTEBOOK") => true,
                _ => false
            };
            if (untouched && !referenced.Contains(product.RowKey)) db.Delete("Products", product.PartitionKey, product.RowKey, product.ETag);
        }
        return added;
    }
}
//-------------------End Of File------------------//

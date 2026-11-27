//summary//
// Uses private pre-provisioned storage and generated object names.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
namespace CloudStore.Uploads;

public sealed class AzureImageStore(BlobServiceClient blobs, IConfiguration configuration) : IImageStore
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient(configuration["AzureStorage:BlobContainer"] ?? configuration["BlobContainer"] ?? "images");
    public async Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default)
    {
        using var content = new MemoryStream(upload.Bytes, writable: false);
        await Container.GetBlobClient(upload.Name).UploadAsync(content, new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = upload.ContentType, ContentDisposition = "attachment" }
        }, cancellationToken);
    }
    public async Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!UploadPolicy.StoredName(name, true)) return null;
        try
        {
            var download = await Container.GetBlobClient(name).DownloadStreamingAsync(cancellationToken: cancellationToken);
            if (download.Value.Details.ContentLength > UploadPolicy.ImageLimit)
            { await download.Value.Content.DisposeAsync(); return null; }
            return new(download.Value.Content, name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}

public sealed class AzureContractStore(ShareClient share, ILogger<AzureContractStore> logger) : IContractStore
{
    public async Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default)
    {
        var file = share.GetRootDirectoryClient().GetFileClient(upload.Name);
        try
        {
            await file.CreateAsync(upload.Bytes.Length, new ShareFileCreateOptions
            {
                HttpHeaders = new ShareFileHttpHeaders { ContentType = upload.ContentType, ContentDisposition = "attachment" },
                Metadata = new Dictionary<string, string> { ["complete"] = "false" }
            }, cancellationToken: cancellationToken);
            using var content = new MemoryStream(upload.Bytes, writable: false);
            await file.UploadAsync(content, cancellationToken: cancellationToken);
            await file.SetMetadataAsync(new Dictionary<string, string> { ["complete"] = "true" }, cancellationToken: cancellationToken);
        }
        catch
        {
            try { await file.DeleteIfExistsAsync(cancellationToken: CancellationToken.None); }
            catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
            { logger.LogWarning("An incomplete contract upload could not be removed; operator review is required."); }
            throw;
        }
    }
    public async Task<List<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string>();
        var inspected = 0;
        var root = share.GetRootDirectoryClient();
        await foreach (var item in root.GetFilesAndDirectoriesAsync(cancellationToken: cancellationToken))
        {
            if (++inspected > 100) break;
            if (item.IsDirectory || !UploadPolicy.StoredName(item.Name, false)) continue;
            try
            {
                var properties = await root.GetFileClient(item.Name).GetPropertiesAsync(cancellationToken: cancellationToken);
                if (properties.Value.Metadata.TryGetValue("complete", out var complete) && complete == "true") names.Add(item.Name);
            }
            catch (RequestFailedException exception) when (exception.Status == 404) { }
        }
        return names;
    }
    public async Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!UploadPolicy.StoredName(name, false)) return null;
        var file = share.GetRootDirectoryClient().GetFileClient(name);
        try
        {
            var properties = await file.GetPropertiesAsync(cancellationToken: cancellationToken);
            if (properties.Value.ContentLength > UploadPolicy.ContractLimit ||
                !properties.Value.Metadata.TryGetValue("complete", out var complete) || complete != "true") return null;
            var download = await file.DownloadAsync(cancellationToken: cancellationToken);
            return new(download.Value.Content, "application/octet-stream");
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
//-------------------End Of File------------------//

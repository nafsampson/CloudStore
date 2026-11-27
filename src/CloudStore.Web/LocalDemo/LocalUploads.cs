//summary//
// Stores validated local uploads outside wwwroot and serves them only through existing authorized actions.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Uploads;
namespace CloudStore.LocalDemo;

public sealed class LocalUploads(DemoSettings settings) : IImageStore, IContractStore
{
    private string PathFor(string name)
    {
        if (!UploadPolicy.StoredName(name, true) && !UploadPolicy.StoredName(name, false)) throw new UploadRejected("Invalid local storage name.");
        return Path.Combine(settings.Directory, "uploads", name);
    }
    public async Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default)
    {
        var path = PathFor(upload.Name);
        var temporary = path + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, useAsync: true))
            {
                await stream.WriteAsync(upload.Bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            // Atomic rename publishes a complete file; an existing name is never overwritten.
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public Task<List<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.EnumerateFiles(Path.Combine(settings.Directory, "uploads"))
            .Select(Path.GetFileName).Where(name => UploadPolicy.StoredName(name, false)).Take(100).Select(name => name!).ToList());
    }
    public Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!UploadPolicy.StoredName(name, true) && !UploadPolicy.StoredName(name, false)) return Task.FromResult<StoredDownload?>(null);
        var path = PathFor(name);
        if (!File.Exists(path) || new FileInfo(path).Length > (UploadPolicy.StoredName(name, true) ? UploadPolicy.ImageLimit : UploadPolicy.ContractLimit)) return Task.FromResult<StoredDownload?>(null);
        var type = Path.GetExtension(name) switch { ".png" => "image/png", ".jpg" => "image/jpeg", _ => "application/octet-stream" };
        return Task.FromResult<StoredDownload?>(new StoredDownload(File.OpenRead(path), type));
    }
}
//-------------------End Of File------------------//

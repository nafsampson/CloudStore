//summary//
// Separates private storage operations from MVC and supports cloud-free boundary tests.
//summary//
//-------------------Start Of File------------------//
namespace CloudStore.Uploads;

public sealed record StoredDownload(Stream Content, string ContentType);
public interface IUploadEvents
{
    Task RecordedAsync(string generatedName, CancellationToken cancellationToken = default);
}
public interface IImageStore
{
    Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default);
    Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default);
}
public interface IContractStore
{
    Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default);
    Task<List<string>> ListAsync(CancellationToken cancellationToken = default);
    Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default);
}
//-------------------End Of File------------------//

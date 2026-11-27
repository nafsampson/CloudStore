//summary//
// Parses a bounded upload contract shared by both isolated-worker upload endpoints.
//summary//
//-------------------Start Of File------------------//
using System.Text;
using System.Text.Json;
namespace CloudStore.Uploads;

public static class FunctionUploadRequest
{
    public static async Task<ValidatedUpload> ParseAsync(Stream body, string contentType, string? filename,
        bool imageTarget, CancellationToken cancellationToken = default)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (type == "application/json")
        {
            var bytes = await UploadPolicy.ReadAsync(body, UploadPolicy.JsonLimit, cancellationToken);
            TextRequest? request;
            try { request = JsonSerializer.Deserialize<TextRequest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 4 }); }
            catch (JsonException) { throw new UploadRejected("Provide a JSON object with fileName and content."); }
            if (request is null || string.IsNullOrEmpty(request.Content) || !string.Equals(Path.GetExtension(request.FileName), ".txt", StringComparison.OrdinalIgnoreCase))
                throw new UploadRejected("JSON text uploads require a .txt filename and non-empty content.");
            return UploadPolicy.Validate(request.FileName, "text/plain", Encoding.UTF8.GetBytes(request.Content), false);
        }
        if (type is not ("application/octet-stream" or "image/png" or "image/jpeg" or "application/pdf" or "text/plain"))
            throw new UploadRejected("Use a raw supported file or application/json; multipart is not accepted by this endpoint.");
        var data = await UploadPolicy.ReadAsync(body, imageTarget ? UploadPolicy.ImageLimit : UploadPolicy.ContractLimit, cancellationToken);
        return UploadPolicy.Validate(filename, type, data, imageTarget);
    }
    private sealed record TextRequest(string? FileName, string? Content);
}
//-------------------End Of File------------------//

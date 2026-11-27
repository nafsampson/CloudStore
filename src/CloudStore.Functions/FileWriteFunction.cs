//summary//
// Provides a key-protected operator upload endpoint with explicit opt-in and shared validation.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using Azure;
using CloudStore.Uploads;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace CloudStore.Functions;

public sealed class FileWriteFunction(IContractStore store, IConfiguration configuration)
{
    [Function("FileWrite")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request,
        CancellationToken cancellationToken)
    {
        async Task<HttpResponseData> Reply(HttpStatusCode status, string message)
        {
            var response = request.CreateResponse(status);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            await response.WriteStringAsync(message, cancellationToken);
            return response;
        }
        if (!bool.TryParse(configuration["EnableStorageHttpWrites"], out var enabled) || !enabled)
            return await Reply(HttpStatusCode.ServiceUnavailable, "Storage HTTP writes are disabled.");
        try
        {
            var contentType = request.Headers.TryGetValues("Content-Type", out var values) ? values.FirstOrDefault() ?? "" : "";
            var filename = System.Web.HttpUtility.ParseQueryString(request.Url.Query).Get("filename");
            var upload = await FunctionUploadRequest.ParseAsync(request.Body, contentType, filename, false, cancellationToken);
            await store.SaveAsync(upload, cancellationToken);
            return await Reply(HttpStatusCode.Created, upload.Name);
        }
        catch (UploadRejected exception) { return await Reply(HttpStatusCode.BadRequest, exception.Message); }
        catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
        { return await Reply(HttpStatusCode.ServiceUnavailable, "Upload could not be confirmed. Check storage before retrying."); }
    }
}
//-------------------End Of File------------------//

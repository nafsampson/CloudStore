//summary//
// Keeps operations notifications best effort; a queue failure cannot undo a saved upload.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Storage.Queues;
namespace CloudStore.Uploads;

public sealed class AzureUploadEvents(QueueClient queue, ILogger<AzureUploadEvents> logger) : IUploadEvents
{
    public async Task RecordedAsync(string generatedName, CancellationToken cancellationToken = default)
    {
        try { await queue.SendMessageAsync("Upload saved: " + generatedName, cancellationToken); }
        catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
        { logger.LogWarning("An upload was saved but its operations notification could not be confirmed."); }
    }
}
//-------------------End Of File------------------//

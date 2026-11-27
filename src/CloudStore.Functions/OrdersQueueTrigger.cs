//summary//
// Processes saved order references; failures propagate to the host's bounded retry/poison-queue handling.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Orders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
namespace CloudStore.Functions;

public sealed class OrdersQueueTrigger(OrderProcessor processor, ILogger<OrdersQueueTrigger> logger)
{
    [Function("OrdersQueueTrigger")]
    public async Task Run([QueueTrigger("%OrdersQueueName%", Connection = "StorageConnection")] string message)
    {
        try
        {
            await processor.ProcessAsync(message);
            logger.LogInformation("CloudStore order delivery completed; outcome ReadyOrDuplicate.");
        }
        catch (InvalidDataException)
        {
            logger.LogWarning("An invalid order reference or saved order requires investigation; delivery will be retried.");
            throw;
        }
        catch (Exception)
        {
            logger.LogError("CloudStore order delivery failed; the host will retry or move it to the poison queue.");
            throw;
        }
        // The host retries storage failures too. No message payload/customer information is logged here.
    }
}
//-------------------End Of File------------------//

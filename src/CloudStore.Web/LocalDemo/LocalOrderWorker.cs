//summary//
// Runs the same order processor in this app, with durable local leases and bounded retry attempts.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Orders;
namespace CloudStore.LocalDemo;

public sealed class LocalOrderPump(DemoDatabase database, IServiceScopeFactory scopes, TimeProvider clock, ILogger<LocalOrderPump> logger)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = database.Claim(clock.GetUtcNow());
        if (message is null) return false;
        using var scope = scopes.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<OrderProcessor>().ProcessAsync(message.Value.Payload);
            database.Complete(message.Value.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The persisted lease becomes eligible after 30 seconds; the fifth failure is retained as poison.
            logger.LogWarning("Local order delivery {DeliveryId} failed on attempt {Attempt}; its payload was not logged.", message.Value.Id, message.Value.Attempts);
        }
        return true;
    }
}
public sealed class LocalOrderWorker(DemoSettings settings, LocalOrderPump pump) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken) { settings.RequireReady(); return base.StartAsync(cancellationToken); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await pump.ProcessOneAsync(stoppingToken);
            await Task.Delay(1000, stoppingToken);
        }
    }
}
//-------------------End Of File------------------//

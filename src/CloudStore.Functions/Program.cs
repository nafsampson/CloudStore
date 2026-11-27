//summary//
// Configures the isolated worker using flat settings shared with the Functions host.
//summary//
//-------------------Start Of File------------------//
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CloudStore.Orders;
using CloudStore.Uploads;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration((context, configuration) => configuration.AddEnvironmentVariables())
    .ConfigureServices((context, services) =>
    {
        // Core Tools exposes local.settings.json Values as environment variables.
        var config = context.Configuration;
        if (!string.IsNullOrWhiteSpace(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            services.AddApplicationInsightsTelemetryWorkerService(options =>
            {
                options.EnableAdaptiveSampling = true;
                options.EnableDependencyTrackingTelemetryModule = false;
            });
            services.ConfigureFunctionsApplicationInsights();
            services.Configure<LoggerFilterOptions>(options =>
            {
                var rule = options.Rules.FirstOrDefault(rule => rule.ProviderName ==
                    "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
                if (rule is not null) options.Rules.Remove(rule);
            });
        }
        var connection = config["StorageConnection"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Missing configuration: StorageConnection. See local.settings.example.json.");
        services.AddSingleton(_ => new TableServiceClient(connection));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOrderStore, AzureOrderStore>();
        services.AddSingleton<OrderProcessor>();
        services.AddSingleton<IImageStore, AzureImageStore>();
        services.AddSingleton<IContractStore, AzureContractStore>();
        services.AddSingleton(_ => new BlobServiceClient(connection));
        services.AddSingleton(_ => new ShareClient(connection, config["FileShareName"] ?? "contracts"));
    })
    .Build();

host.Run();
//-------------------End Of File------------------//

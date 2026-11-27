//summary//
// Registers optional Azure telemetry and removes sensitive URL and SQL diagnostic tags.
//summary//
//-------------------Start Of File------------------//
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace CloudStore.Monitoring;

public static class CloudStoreTelemetry
{
    public const string Name = "CloudStore.Orders";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Processing = Meter.CreateCounter<long>("cloudstore.orders.processing");

    public static void AddCloudStoreMonitoring(this IServiceCollection services, IConfiguration config, bool local)
    {
        // LocalDemo never exports telemetry, even when the parent shell has Azure settings.
        if (local || string.IsNullOrWhiteSpace(config["APPLICATIONINSIGHTS_CONNECTION_STRING"])) return;
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("cloudstore-web"))
            .WithTracing(tracing => tracing.AddSource(Name).AddProcessor(new PrivacyProcessor()))
            .WithMetrics(metrics => metrics.AddMeter(Name))
            .UseAzureMonitor(options =>
            {
                options.ConnectionString = config["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                options.EnableLiveMetrics = false;
                options.SamplingRatio = 0.1F;
            });
    }
}

// Automatic HTTP instrumentation must not export query strings or credential-bearing URLs.
public sealed class PrivacyProcessor : OpenTelemetry.BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        foreach (var key in new[] { "url.full", "http.url" })
        {
            if (activity.GetTagItem(key) is string value && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                var safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
                activity.SetTag(key, safe.Uri.GetLeftPart(UriPartial.Path));
            }
        }
        foreach (var key in new[] { "url.query", "http.target", "db.statement", "db.query.text" })
            activity.SetTag(key, null);
    }
}
//-------------------End Of File------------------//

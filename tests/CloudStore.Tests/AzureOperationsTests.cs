//summary//
// Verifies startup storage validation, telemetry privacy and cloud-independent liveness.
//summary//
//-------------------Start Of File------------------//
using System.Diagnostics;
using CloudStore.Monitoring;
using CloudStore.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace CloudStore.Tests;

public sealed class AzureOperationsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a-raw-account-key-is-not-a-connection-string")]
    [InlineData("AccountName=example")]
    public void Malformed_storage_is_rejected_without_exposing_the_value(string value)
        => Assert.False(new RuntimeConnections { Storage = value }.HasValidStorage());

    [Fact]
    public void Complete_storage_connection_parses_without_contacting_Azure()
    {
        var dummy = Convert.ToBase64String(new byte[64]);
        Assert.True(new RuntimeConnections { Storage = $"DefaultEndpointsProtocol=https;AccountName=example;AccountKey={dummy};EndpointSuffix=core.windows.net" }.HasValidStorage());
    }

    [Fact]
    public void Telemetry_removes_query_strings_credentials_and_SQL_text()
    {
        using var activity = new Activity("privacy-test").Start();
        activity.SetTag("url.full", "https://user:password@example.com/path?token=private#fragment");
        activity.SetTag("http.url", "https://example.com/path?secret=private");
        activity.SetTag("db.statement", "SELECT private-data");
        activity.SetTag("db.query.text", "SELECT private-data");
        activity.SetTag("url.query", "token=private");
        new PrivacyProcessor().OnEnd(activity);
        Assert.Equal("https://example.com/path", activity.GetTagItem("url.full"));
        Assert.Equal("https://example.com/path", activity.GetTagItem("http.url"));
        Assert.Null(activity.GetTagItem("db.statement"));
        Assert.Null(activity.GetTagItem("db.query.text"));
        Assert.Null(activity.GetTagItem("url.query"));
    }

    [Fact]
    public void Local_demo_does_not_register_cloud_exporters_with_inherited_Azure_settings()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "invalid-inherited-setting"
        }).Build();
        services.AddCloudStoreMonitoring(config, local: true);
        Assert.Empty(services);
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_needs_no_Azure_or_login()
    {
        await using var directory = new DemoDirectory();
        await directory.Initialize();
        await using var app = CloudStore.Program.CreateApplication(directory.Builder());
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
//-------------------End Of File------------------//

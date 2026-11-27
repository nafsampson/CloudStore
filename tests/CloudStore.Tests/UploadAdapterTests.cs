//summary//
// Verifies SDK write conditions and partial-file completion using local client subclasses.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using CloudStore.Uploads;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace CloudStore.Tests;

public sealed class UploadAdapterTests
{
    [Fact]
    public async Task Failed_notification_does_not_turn_saved_upload_into_a_failure()
    {
        var events = new AzureUploadEvents(new FailedQueue(), NullLogger<AzureUploadEvents>.Instance);
        await events.RecordedAsync(new string('a', 32) + ".txt", TestContext.Current.CancellationToken);
    }
    private sealed class FailedQueue : QueueClient
    {
        public override Task<Response<SendReceipt>> SendMessageAsync(string message, CancellationToken cancellationToken = default)
            => throw new RequestFailedException(503, "Unavailable");
    }
    [Fact]
    public async Task Blob_adapter_uses_configured_private_container_and_create_only_condition()
    {
        var client = new LocalBlobs();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AzureStorage:BlobContainer"] = "private-images" }).Build();
        var store = new AzureImageStore(client, configuration);
        var upload = UploadPolicy.Validate("photo.png", "image/png", UploadPolicyTests.Png, true);
        await store.SaveAsync(upload, TestContext.Current.CancellationToken);
        Assert.Equal("private-images", client.CapturedName);
        Assert.Equal(upload.Name, client.Container.CapturedName);
        Assert.Equal(ETag.All, client.Container.Blob.Options!.Conditions.IfNoneMatch);
        Assert.Equal("image/png", client.Container.Blob.Options.HttpHeaders.ContentType);
        Assert.Equal("attachment", client.Container.Blob.Options.HttpHeaders.ContentDisposition);
        Assert.Equal(upload.Bytes, client.Container.Blob.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contract_completion_is_written_only_after_upload_and_failure_attempts_cleanup(bool fail)
    {
        var share = new LocalShare();
        share.Directory.File.Fail = fail;
        var store = new AzureContractStore(share, NullLogger<AzureContractStore>.Instance);
        var upload = UploadPolicy.Validate("contract.txt", "text/plain", "Terms"u8.ToArray(), false);
        if (fail) await Assert.ThrowsAsync<RequestFailedException>(() => store.SaveAsync(upload, TestContext.Current.CancellationToken));
        else await store.SaveAsync(upload, TestContext.Current.CancellationToken);
        Assert.Equal(upload.Name, share.Directory.CapturedName);
        Assert.Equal("false", share.Directory.File.InitialComplete);
        Assert.Equal(fail ? new[] { "create", "upload", "delete" } : new[] { "create", "upload", "complete" }, share.Directory.File.Operations);
        Assert.Equal("attachment", share.Directory.File.Headers!.ContentDisposition);
    }

    [Fact]
    public async Task Invalid_storage_identifiers_are_rejected_before_any_SDK_client_use()
    {
        var image = new AzureImageStore(new LocalBlobs(), new ConfigurationBuilder().Build());
        var contract = new AzureContractStore(new LocalShare(), NullLogger<AzureContractStore>.Instance);
        Assert.Null(await image.ReadAsync("../private.png", TestContext.Current.CancellationToken));
        Assert.Null(await contract.ReadAsync("https://elsewhere/contract.pdf", TestContext.Current.CancellationToken));
    }

    private sealed class LocalBlobs : BlobServiceClient
    {
        public string? CapturedName;
        public LocalContainer Container { get; } = new();
        public override BlobContainerClient GetBlobContainerClient(string name) { CapturedName = name; return Container; }
    }
    private sealed class LocalContainer : BlobContainerClient
    {
        public string? CapturedName;
        public LocalBlob Blob { get; } = new();
        public override BlobClient GetBlobClient(string name) { CapturedName = name; return Blob; }
    }
    private sealed class LocalBlob : BlobClient
    {
        public BlobUploadOptions? Options;
        public byte[]? Bytes;
        public override async Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            Options = options;
            using var capture = new MemoryStream();
            await content.CopyToAsync(capture, cancellationToken);
            Bytes = capture.ToArray();
            return Response.FromValue<BlobContentInfo>(null!, new TestResponse(201));
        }
    }
    private sealed class LocalShare : ShareClient
    {
        public LocalDirectory Directory { get; } = new();
        public override ShareDirectoryClient GetRootDirectoryClient() => Directory;
    }
    private sealed class LocalDirectory : ShareDirectoryClient
    {
        public string? CapturedName;
        public LocalFile File { get; } = new();
        public override ShareFileClient GetFileClient(string name) { CapturedName = name; return File; }
    }
    private sealed class LocalFile : ShareFileClient
    {
        public bool Fail;
        public string? InitialComplete;
        public ShareFileHttpHeaders? Headers;
        public List<string> Operations { get; } = new();
        public override Task<Response<ShareFileInfo>> CreateAsync(long maxSize, ShareFileCreateOptions options, ShareFileRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            Operations.Add("create");
            InitialComplete = options.Metadata["complete"];
            Headers = options.HttpHeaders;
            return Task.FromResult(Response.FromValue<ShareFileInfo>(null!, new TestResponse(201)));
        }
        public override Task<Response<ShareFileUploadInfo>> UploadAsync(Stream content, ShareFileUploadOptions? options = null, CancellationToken cancellationToken = default)
        {
            Operations.Add("upload");
            if (Fail) throw new RequestFailedException(503, "Interrupted upload");
            return Task.FromResult(Response.FromValue<ShareFileUploadInfo>(null!, new TestResponse(201)));
        }
        public override Task<Response<ShareFileInfo>> SetMetadataAsync(IDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        {
            Assert.Equal("true", metadata["complete"]);
            Operations.Add("complete");
            return Task.FromResult(Response.FromValue<ShareFileInfo>(null!, new TestResponse(200)));
        }
        public override Task<Response<ShareFileInfo>> SetMetadataAsync(IDictionary<string, string> metadata, ShareFileRequestConditions? conditions = null, CancellationToken cancellationToken = default)
            => SetMetadataAsync(metadata, cancellationToken);
        public override Task<Response<bool>> DeleteIfExistsAsync(ShareFileRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        { Operations.Add("delete"); return Task.FromResult(Response.FromValue(true, new TestResponse(202))); }
    }
}
//-------------------End Of File------------------//

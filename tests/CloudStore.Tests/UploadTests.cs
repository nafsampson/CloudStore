//summary//
// Exercises upload validation, private delivery and multipart permissions without cloud access.
//summary//
//-------------------Start Of File------------------//
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure;
using CloudStore.Models;
using CloudStore.Uploads;
using Xunit;

namespace CloudStore.Tests;

public sealed class MemoryUploads : IImageStore, IContractStore, IUploadEvents
{
    public Dictionary<string, ValidatedUpload> Saved { get; } = new();
    public List<string> Events { get; } = new();
    public int Reads { get; private set; }
    public bool FailWrite { get; set; }
    public Task SaveAsync(ValidatedUpload upload, CancellationToken cancellationToken = default)
    {
        if (FailWrite) throw new RequestFailedException(503, "Storage unavailable");
        Saved.Add(upload.Name, upload);
        return Task.CompletedTask;
    }
    public Task<List<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Saved.Keys.Where(name => UploadPolicy.StoredName(name, false)).ToList());
    public Task<StoredDownload?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Saved.TryGetValue(name, out var upload) ? new StoredDownload(new MemoryStream(upload.Bytes), upload.ContentType) : null);
    }
    public Task RecordedAsync(string name, CancellationToken cancellationToken = default) { Events.Add(name); return Task.CompletedTask; }
}

public sealed class UploadPolicyTests
{
    public static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1EAAAAASUVORK5CYII=");

    [Fact]
    public void Same_original_filename_produces_different_safe_storage_keys()
    {
        var first = UploadPolicy.Validate("image.PNG", "image/png", Png, true);
        var second = UploadPolicy.Validate("image.PNG", "image/png", Png, true);
        Assert.NotEqual(first.Name, second.Name);
        Assert.True(UploadPolicy.StoredName(first.Name, true));
        Assert.Equal("image/png", first.ContentType);
    }

    [Theory]
    [InlineData("../image.png")]
    [InlineData("folder/image.png")]
    [InlineData("C:\\image.png")]
    [InlineData("image%2epng")]
    [InlineData("image\r\n.png")]
    [InlineData("image.svg")]
    [InlineData("image.html")]
    [InlineData("image.png.exe")]
    public void Unsafe_or_unsupported_name_is_rejected(string name) =>
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate(name, "image/png", Png, true));

    [Fact]
    public void Renamed_html_wrong_mime_truncated_and_oversized_images_are_rejected()
    {
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("photo.png", "image/png", "<script>alert(1)</script>"u8.ToArray(), true));
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("photo.png", "text/html", Png, true));
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("photo.png", "image/png", Png[..20], true));
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("photo.png", "image/png", new byte[UploadPolicy.ImageLimit + 1], true));
        var huge = Png;
        huge[16] = 127;
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("photo.png", "image/png", huge, true));
    }

    [Theory]
    [InlineData("notes.txt", "text/plain", "Simple contract\r\nTerms\taccepted.")]
    [InlineData("contract.pdf", "application/pdf", "%PDF-1.7\n1 0 obj\n%%EOF")]
    public void Supported_contract_headers_and_text_are_accepted(string name, string type, string content)
    {
        var result = UploadPolicy.Validate(name, type, Encoding.UTF8.GetBytes(content), false);
        Assert.True(UploadPolicy.StoredName(result.Name, false));
        Assert.Equal(type, result.ContentType);
    }

    [Fact]
    public void Binary_disguised_as_text_and_invalid_pdf_are_rejected()
    {
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("notes.txt", "text/plain", [255, 254, 0], false));
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("notes.txt", "text/plain", [65, 0, 66], false));
        Assert.Throws<UploadRejected>(() => UploadPolicy.Validate("notes.pdf", "application/pdf", "not a pdf"u8.ToArray(), false));
    }

    [Fact]
    public async Task Bounded_reader_checks_actual_bytes_and_empty_input()
    {
        await Assert.ThrowsAsync<UploadRejected>(() => UploadPolicy.ReadAsync(new MemoryStream(new byte[11]), 10, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UploadRejected>(() => UploadPolicy.ReadAsync(new MemoryStream(), 10, TestContext.Current.CancellationToken));
        Assert.Equal(10, (await UploadPolicy.ReadAsync(new MemoryStream(new byte[10]), 10, TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("{broken", "application/json")]
    [InlineData("null", "application/json")]
    [InlineData("{\"fileName\":\"../notes.txt\",\"content\":\"terms\"}", "application/json")]
    [InlineData("{\"fileName\":\"notes.exe\",\"content\":\"terms\"}", "application/json")]
    [InlineData("arbitrary multipart bytes", "multipart/form-data")]
    public async Task Worker_upload_parser_rejects_bad_json_paths_and_multipart(string body, string type)
    {
        await Assert.ThrowsAsync<UploadRejected>(() => FunctionUploadRequest.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(body)), type, null, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Worker_upload_parser_preserves_text_json_and_raw_image_features()
    {
        var text = await FunctionUploadRequest.ParseAsync(new MemoryStream("{\"fileName\":\"notes.txt\",\"content\":\"terms\"}"u8.ToArray()),
            "application/json", null, false, TestContext.Current.CancellationToken);
        Assert.Equal("terms", Encoding.UTF8.GetString(text.Bytes));
        Assert.True(UploadPolicy.StoredName(text.Name, false));
        var image = await FunctionUploadRequest.ParseAsync(new MemoryStream(Png), "application/octet-stream", "photo.png", true, TestContext.Current.CancellationToken);
        Assert.Equal("image/png", image.ContentType);
    }
}

public sealed class UploadHttpTests(RetailFactory app) : IClassFixture<RetailFactory>
{
    [Fact]
    public async Task Product_edit_preserves_saved_image_and_uses_existing_ETag_in_merge()
    {
        using var browser = await Login("administrator");
        var key = Guid.NewGuid().ToString("N");
        var name = new string('b', 32) + ".png";
        app.Tables.Table("Products").Rows.Add(new Product { RowKey = key, Name = "Original", Sku = "SKU", Price = "10", ImageName = name, ETag = new ETag("original-version") });
        var token = await RetailFactory.Token(browser);
        using var response = await browser.PostAsync("/Tables/EditProduct", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["RowKey"] = key, ["Name"] = "Updated", ["Sku"] = "SKU", ["Price"] = "11",
            ["version"] = "original-version", ["ImageName"] = "attacker.png", ["ImageUrl"] = "https://attacker.invalid",
            ["__RequestVerificationToken"] = token
        }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var table = app.Tables.Table("Products");
        Assert.Equal(new ETag("original-version"), table.LastPatchETag);
        Assert.Equal(Azure.Data.Tables.TableUpdateMode.Merge, table.LastPatchMode);
        Assert.False(table.LastPatch!.ContainsKey("ImageName"));
        Assert.Equal("", table.LastPatch["ImageUrl"]);
        Assert.Equal("Updated", table.LastPatch["Name"]);
    }

    [Fact]
    public async Task Product_edit_rejects_stale_form_before_writing_image()
    {
        using var browser = await Login("administrator");
        var key = Guid.NewGuid().ToString("N");
        app.Tables.Table("Products").Rows.Add(new Product { RowKey = key, Name = "Newer", Sku = "SKU", Price = "10", ETag = new ETag("new-version") });
        var before = app.Uploads.Saved.Count;
        using var form = await Multipart(browser, UploadPolicyTests.Png, "photo.png", "imageFile", "image/png");
        foreach (var (field, value) in new[] { ("RowKey", key), ("Name", "Older"), ("Sku", "SKU"), ("Price", "9"), ("version", "old-version") })
            form.Add(new StringContent(value), field);
        Assert.Equal(HttpStatusCode.Conflict, (await browser.PostAsync("/Tables/EditProduct", form, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(before, app.Uploads.Saved.Count);
    }

    [Fact]
    public async Task Storage_failure_does_not_claim_contract_success_or_send_notification()
    {
        using var browser = await Login("administrator");
        var before = app.Uploads.Events.Count;
        app.Uploads.FailWrite = true;
        try
        {
            using var form = await Multipart(browser, "Terms"u8.ToArray(), "contract.txt", "file", "text/plain");
            Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Files/Upload", form, TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(before, app.Uploads.Events.Count);
            var page = await browser.GetStringAsync("/Files", TestContext.Current.CancellationToken);
            Assert.Contains("Upload could not be confirmed", page);
        }
        finally { app.Uploads.FailWrite = false; }
    }

    private async Task<HttpClient> Login(string username)
    {
        var browser = app.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await app.Login(browser, username)).StatusCode);
        return browser;
    }
    private static async Task<MultipartFormDataContent> Multipart(HttpClient browser, byte[] bytes, string name, string field, string type, bool token = true)
    {
        var form = new MultipartFormDataContent();
        if (token) form.Add(new StringContent(await RetailFactory.Token(browser)), "__RequestVerificationToken");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(file, field, name);
        return form;
    }

    [Fact]
    public async Task Admin_uploads_contract_as_generated_name_and_downloads_attachment()
    {
        using var browser = await Login("administrator");
        var before = app.Uploads.Saved.Count;
        using var form = await Multipart(browser, "Terms"u8.ToArray(), "contract.txt", "file", "text/plain");
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Files/Upload", form, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(before + 1, app.Uploads.Saved.Count);
        var name = app.Uploads.Events.Last();
        Assert.True(UploadPolicy.StoredName(name, false));
        var response = await browser.GetAsync("/Files/Download?name=" + name, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Terms", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("customer-a", true, HttpStatusCode.Forbidden)]
    [InlineData("administrator", false, HttpStatusCode.BadRequest)]
    public async Task Upload_requires_administrator_and_antiforgery(string username, bool token, HttpStatusCode expected)
    {
        using var browser = await Login(username);
        var before = app.Uploads.Saved.Count;
        using var form = await Multipart(browser, "Terms"u8.ToArray(), "contract.txt", "file", "text/plain", token);
        Assert.Equal(expected, (await browser.PostAsync("/Files/Upload", form, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(before, app.Uploads.Saved.Count);
    }

    [Fact]
    public async Task Invalid_contract_is_not_written_and_unsafe_download_does_not_reach_storage()
    {
        using var browser = await Login("administrator");
        var before = app.Uploads.Saved.Count;
        using var form = await Multipart(browser, "<script>bad</script>"u8.ToArray(), "contract.pdf", "file", "application/pdf");
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Files/Upload", form, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(before, app.Uploads.Saved.Count);
        var reads = app.Uploads.Reads;
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/Files/Download?name=..%2Fsecret.txt", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(reads, app.Uploads.Reads);
    }

    [Fact]
    public async Task Product_ignores_forged_image_fields_and_serves_private_image_through_product_id()
    {
        using var admin = await Login("administrator");
        using var form = await Multipart(admin, UploadPolicyTests.Png, "photo.png", "imageFile", "image/png");
        form.Add(new StringContent("Test product"), "Name");
        form.Add(new StringContent("SAFE-1"), "Sku");
        form.Add(new StringContent("12.50"), "Price");
        form.Add(new StringContent("https://attacker.invalid/image.svg"), "ImageUrl");
        form.Add(new StringContent("../secret"), "ImageName");
        Assert.Equal(HttpStatusCode.Redirect, (await admin.PostAsync("/Tables/AddProduct", form, TestContext.Current.CancellationToken)).StatusCode);
        var product = app.Tables.Table("Products").Rows.OfType<Product>().Last();
        Assert.Null(product.ImageUrl);
        Assert.True(UploadPolicy.StoredName(product.ImageName, true));
        using var customer = await Login("customer-a");
        var image = await customer.GetAsync("/Tables/Image?id=" + product.RowKey, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(UploadPolicyTests.Png, await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        var page = await customer.GetStringAsync("/Tables/Products", TestContext.Current.CancellationToken);
        Assert.Contains("/Tables/Image", page);
        Assert.DoesNotContain("attacker.invalid", page);
        using var anonymous = app.Browser();
        var denied = await anonymous.GetAsync("/Tables/Image?id=" + product.RowKey, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
    }
}
//-------------------End Of File------------------//

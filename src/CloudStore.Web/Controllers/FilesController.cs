//summary//
// Keeps contract uploads and attachment downloads within the administrator boundary.
//summary//
//-------------------Start Of File------------------//
using Azure;
using CloudStore.Security;
using CloudStore.Uploads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace CloudStore.Controllers;

[Authorize(Policy = AccessPolicies.Administrator)]
public sealed class FilesController(IContractStore contracts, IUploadEvents events) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await contracts.ListAsync(cancellationToken));

    [HttpPost, RequestSizeLimit(UploadPolicy.ContractLimit + 65536), RequestFormLimits(MultipartBodyLengthLimit = UploadPolicy.ContractLimit + 65536)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken cancellationToken)
    {
        try
        {
            if (file is null || file.Length <= 0 || file.Length > UploadPolicy.ContractLimit) throw new UploadRejected("Select a PDF or text file up to 4 MiB.");
            await using var stream = file.OpenReadStream();
            var bytes = await UploadPolicy.ReadAsync(stream, UploadPolicy.ContractLimit, cancellationToken);
            if (bytes.Length != file.Length) throw new UploadRejected("The uploaded file length is invalid.");
            var upload = UploadPolicy.Validate(file.FileName, file.ContentType, bytes, false);
            await contracts.SaveAsync(upload, cancellationToken);
            await events.RecordedAsync(upload.Name, cancellationToken);
            TempData["Message"] = "Contract uploaded.";
        }
        catch (UploadRejected exception) { TempData["Error"] = exception.Message; }
        catch (Exception exception) when (exception is RequestFailedException or IOException or HttpRequestException)
        { TempData["Error"] = "Upload could not be confirmed. Check the contract list before trying again."; }
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Download(string name, CancellationToken cancellationToken)
    {
        if (!UploadPolicy.StoredName(name, false)) return NotFound();
        var download = await contracts.ReadAsync(name, cancellationToken);
        if (download is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "no-store";
        return File(download.Content, "application/octet-stream", name);
    }
}
//-------------------End Of File------------------//

//summary//
// Exposes administrator-only inspection of the operations message log.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authorization;
using CloudStore.Security;
using CloudStore.Services;
using Microsoft.AspNetCore.Mvc;

namespace CloudStore.Controllers;

// Controller to send and view messages from an Azure Queue
[Authorize(Policy = AccessPolicies.Administrator)]
public class QueueController : Controller
{
    private readonly IOperationsLog _queue;
    public QueueController(IOperationsLog queue) => _queue = queue;


    public async Task<IActionResult> Index()
    {
        var msgs = await _queue.PeekAsync(HttpContext.RequestAborted);
        return View(msgs);
    }

    // Sends a new message to the queue
    [HttpPost]
    public async Task<IActionResult> Send(string text)
    {
        if (text?.Length > 2048) return BadRequest("Use at most 2048 characters.");
        if (!string.IsNullOrWhiteSpace(text))
            await _queue.SendAsync(text);
        return RedirectToAction(nameof(Index));
    }
}
//-------------------End Of File------------------//

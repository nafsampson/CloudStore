//summary//
// Serves the application overview, data-handling information and safe error page.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Mvc;

namespace CloudStore.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            // Just show a welcome or dashboard page
            return View();
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [Microsoft.AspNetCore.Authorization.AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View(new CloudStore.Models.ErrorViewModel
        {
            RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
}
//-------------------End Of File------------------//

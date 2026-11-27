//summary//
// Serves the administrator dashboard behind the administrator authorization policy.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudStore.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AdminController : Controller
    {
        public IActionResult Index() => View();
    }
}
//-------------------End Of File------------------//

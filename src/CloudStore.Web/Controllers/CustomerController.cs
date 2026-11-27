//summary//
// Serves the customer dashboard under the retail-user authorization policy.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudStore.Controllers
{
    [Authorize(Roles = "Customer")]
    public class CustomerController : Controller
    {
        public IActionResult Index() => View();
    }
}
//-------------------End Of File------------------//

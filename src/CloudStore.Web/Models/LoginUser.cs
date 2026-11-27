//summary//
// Stores account identity, hashed credentials and account status through ASP.NET Core Identity.
//summary//
//-------------------Start Of File------------------//
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using CloudStore.Security;

namespace CloudStore.Models;

public class LoginUser : IdentityUser<int>
{
    [Required, MaxLength(20)]
    public string Role { get; set; } = AccessPolicies.CustomerRole;
    public bool IsEnabled { get; set; } = true;
}
//-------------------End Of File------------------//

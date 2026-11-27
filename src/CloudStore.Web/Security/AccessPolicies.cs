//summary//
// Defines server-enforced roles and the stable account identifier used for storage ownership.
//summary//
//-------------------Start Of File------------------//
using System.Globalization;
using System.Security.Claims;

namespace CloudStore.Security;

public static class AccessPolicies
{
    public const string AdminRole = "Admin";
    public const string CustomerRole = "Customer";
    public const string RetailUser = "RetailUser";
    public const string Administrator = "Administrator";
    public const string Customer = "CustomerAccount";
    public static bool IsKnownRole(string role) => role is AdminRole or CustomerRole;

    public static string AccountId(ClaimsPrincipal user)
    {
        var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(claim, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new InvalidOperationException("The authenticated session has no valid account identifier.");
        return id.ToString(CultureInfo.InvariantCulture);
    }
}
//-------------------End Of File------------------//

//summary//
// Adds the database-controlled account role to the standard Identity claims.
//summary//
//-------------------Start Of File------------------//
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using CloudStore.Models;

namespace CloudStore.Security;

public class RetailClaimsFactory(UserManager<LoginUser> manager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<LoginUser>(manager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(LoginUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (AccessPolicies.IsKnownRole(user.Role)) identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        return identity;
    }
}
//-------------------End Of File------------------//

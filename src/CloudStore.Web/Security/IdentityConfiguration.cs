//summary//
// Shares account policy between MVC login and the operator-only provisioning command.
//summary//
//-------------------Start Of File------------------//
using CloudStore.Data;
using CloudStore.Models;
using Microsoft.AspNetCore.Identity;

namespace CloudStore.Security;

public static class IdentityConfiguration
{
    public static IdentityBuilder AddRetailIdentity(this IServiceCollection services)
    {
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        return services.AddIdentityCore<LoginUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireDigit = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;
        }).AddEntityFrameworkStores<AppDbContext>();
    }
}
//-------------------End Of File------------------//

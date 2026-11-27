//summary//
// Persists Identity accounts; schema migrations are applied separately from application startup.
//summary//
//-------------------Start Of File------------------//
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using CloudStore.Models;

namespace CloudStore.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<LoginUser, int>(options)
{
    public DbSet<LoginUser> LoginUsers => Users;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<LoginUser>().ToTable("LoginUsers");
        builder.Entity<LoginUser>().HasIndex(user => user.Role);
        // Keep Identity composite keys within SQL Server's clustered-index byte limit.
        builder.Entity<IdentityUserLogin<int>>().Property(login => login.LoginProvider).HasMaxLength(128);
        builder.Entity<IdentityUserLogin<int>>().Property(login => login.ProviderKey).HasMaxLength(128);
        builder.Entity<IdentityUserToken<int>>().Property(token => token.LoginProvider).HasMaxLength(128);
        builder.Entity<IdentityUserToken<int>>().Property(token => token.Name).HasMaxLength(128);
    }
}
//-------------------End Of File------------------//

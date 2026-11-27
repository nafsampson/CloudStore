//summary//
// Creates fresh accounts through Identity; never imports or compares legacy plaintext passwords.
//summary//
//-------------------Start Of File------------------//
using System.Data;
using CloudStore.Data;
using CloudStore.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloudStore.Security;

public sealed class AccountProvisioner(AppDbContext database, UserManager<LoginUser> users)
{
    public async Task<IdentityResult> CreateAsync(string username, string password, bool firstAdministrator,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username != username.Trim())
            return IdentityResult.Failed(new IdentityError { Code = "InvalidUserName", Description = "Enter a valid username without surrounding spaces (maximum 256 characters)." });

        // SQL Server serializable isolation holds the administrator range until commit,
        // preventing two operators from both creating the first administrator.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (firstAdministrator && await database.Users.AnyAsync(user => user.Role == AccessPolicies.AdminRole, cancellationToken))
            return IdentityResult.Failed(new IdentityError { Code = "AdministratorExists", Description = "An administrator already exists. This command cannot replace or reset it." });
        var account = new LoginUser
        {
            UserName = username,
            Role = firstAdministrator ? AccessPolicies.AdminRole : AccessPolicies.CustomerRole,
            IsEnabled = true
        };
        var result = await users.CreateAsync(account, password);
        if (result.Succeeded) await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
//-------------------End Of File------------------//

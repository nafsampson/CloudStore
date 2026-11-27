//summary//
// Resets an existing account through Identity from an authenticated operator command.
// Atomically replaces the password, invalidates sessions and clears failed-login lockout.
// Preserves account IDs, roles, enabled state and all retail data.
//summary//
//-------------------Start Of File------------------//
using System.Data;
using CloudStore.Data;
using CloudStore.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CloudStore.Security;

public sealed class AccountPasswordReset(AppDbContext database, UserManager<LoginUser> users)
{
    public async Task<IdentityResult> ResetAsync(string username, string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username != username.Trim())
            return Failure("InvalidUserName");
        if (password.Length > 256) return Failure("PasswordTooLong");
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await users.FindByNameAsync(username);
        if (user is null) return Failure("AccountNotFound");
        if (!AccessPolicies.IsKnownRole(user.Role)) return Failure("InvalidAccountRole");

        // Token generation and consumption stay in this process; no token or hash is printed.
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded) return result;
        result = await users.ResetAccessFailedCountAsync(user);
        if (!result.Succeeded) return result;
        result = await users.SetLockoutEndDateAsync(user, null);
        if (!result.Succeeded) return result;
        await transaction.CommitAsync(cancellationToken);
        return IdentityResult.Success;
    }

    private static IdentityResult Failure(string code) => IdentityResult.Failed(new IdentityError { Code = code, Description = code });
}
//-------------------End Of File------------------//

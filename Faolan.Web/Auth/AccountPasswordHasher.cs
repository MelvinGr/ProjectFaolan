using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Identity;

namespace Faolan.Web.Auth
{
	// Bridges Identity's UserManager to the SHARED PasswordHashing format, so the game server's login stays
	// compatible with the web one - one Account.Password column serves both.
	//
	// SuccessRehashNeeded is never returned: it existed to migrate legacy PLAINTEXT values, and there is no plaintext
	// path any more. If the PBKDF2 iteration count is ever raised, this is where the re-hash prompt goes back in -
	// keyed off the iteration count parsed from the stored hash, not off "the stored value isn't a hash".
	public sealed class AccountPasswordHasher : IPasswordHasher<Account>
	{
		public string HashPassword(Account user, string password)
		{
			return PasswordHashing.Hash(password);
		}

		public PasswordVerificationResult VerifyHashedPassword(Account user, string hashedPassword, string providedPassword)
		{
			return PasswordHashing.Verify(providedPassword, hashedPassword)
				? PasswordVerificationResult.Success
				: PasswordVerificationResult.Failed;
		}
	}
}

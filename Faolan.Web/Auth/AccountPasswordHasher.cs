using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Identity;

namespace Faolan.Web.Auth
{
	// Bridges Identity's UserManager to the shared PasswordHashing format, so the game server's login stays compatible
	// with the web one - one Account.Password column serves both.
	//
	// SuccessRehashNeeded is never returned, because there is no legacy plaintext path. If the PBKDF2 iteration count
	// is ever raised, this is where the re-hash prompt goes - keyed off the iteration count parsed from the stored
	// hash, never off "the stored value is not a hash".
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

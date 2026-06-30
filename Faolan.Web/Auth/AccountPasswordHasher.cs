using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Microsoft.AspNetCore.Identity;

namespace Faolan.Web.Auth
{
	// Bridges Identity's UserManager to the SHARED PasswordHashing format (so the game server's login stays
	// compatible). Verifying a legacy plaintext value returns SuccessRehashNeeded, which makes UserManager
	// transparently re-store it as a hash after a successful login.
	public sealed class AccountPasswordHasher : IPasswordHasher<Account>
	{
		public string HashPassword(Account user, string password)
		{
			return PasswordHashing.Hash(password);
		}

		public PasswordVerificationResult VerifyHashedPassword(Account user, string hashedPassword, string providedPassword)
		{
			if (!PasswordHashing.Matches(hashedPassword, providedPassword))
				return PasswordVerificationResult.Failed;

			return PasswordHashing.NeedsRehash(hashedPassword)
				? PasswordVerificationResult.SuccessRehashNeeded
				: PasswordVerificationResult.Success;
		}
	}
}

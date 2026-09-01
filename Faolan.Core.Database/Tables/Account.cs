using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(UserName), IsUnique = true)]
	public class Account : BaseEntity
	{
		public virtual ICollection<AccountClaim> Claims { get; set; } = [];

		public virtual ICollection<Character> Characters { get; set; } = [];

		[ConstrainToEnumValues]
		public AccountState State { get; set; }

		[ConstrainToEnumValues]
		public WebAccountType Type { get; set; }

		public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.UtcNow;
		public DateTimeOffset? LastConnection { get; set; }

		// ⭐ When a TIMED ban lifts itself (#918). NULL for an indefinite ban (`.ban`) and for an account that is not
		// banned at all; a time in the past means the ban has expired and the next login clears it.
		//
		// The lift is a property of the DATA rather than of a running process, which is the whole point: `.kickban`
		// used to schedule it with an in-process `Task.Delay`, so a restart inside the window left the account banned
		// until someone ran `.unban` by hand - and the reverse, a restart could not shorten it either.
		//
		// ⚠️ It is deliberately NOT a second source of truth for "is this account banned". `State` remains that; this
		// only says when the state should stop applying. A row with `State = Banned` and an elapsed `BanExpiresAt` is
		// still banned until something reads it - `IDatabaseRepository.ClearExpiredBan` at login is what makes it so,
		// and it is the one place that transition happens.
		public DateTimeOffset? BanExpiresAt { get; set; }

		// Whether the account has UNSEEN claims - drives the "new claims available" glow on the client's claims button
		// (CoreClaimIIR sub 0x0009). Set true when a claim is added; cleared when the player opens the Claims window
		// (the list request). One flag per account, not per-claim. See GameServerListener.Claims.cs / #359.
		public bool ClaimsAvailable { get; set; }

		// ⭐ THE ACCOUNT'S GM PRIVILEGES, and the only place they live: the retail client's `gm_level` MASK (character
		// stat 0x0D7 / 215, `GmPrivileges`), or NULL for an account that is not a GM at all.
		//
		// One nullable column rather than the four things this used to be - an `WebAccountType`-derived role, an
		// explicit override mask, and separate `GmGodMode`/`GmInvisible` booleans - because three of those were the
		// SAME data written twice: `GodMode` is bit 0x200 and `InvisibleToMonsters` is bit 0x80, both already in the
		// mask retail puts on the wire. Two stores of one fact is how they drift, and #902 is what that costs.
		//
		// ⚠️ NULL and 0 are the same thing on the wire and deliberately not distinguished: ANY non-zero value flips the
		// retail client into GM mode, so "a GM with no privileges" is not representable - an account with every bit
		// cleared simply is not a GM. `IsGm` is therefore `GmLevel is not null`, not a second column that could disagree.
		//
		// ⚠️ This is NOT `WebAccountType`. That is the web ADMIN site's role and says nothing about the game; an account
		// can administer the site without being a GM in-world, and vice versa.
		public uint? GmLevel { get; set; }

		/// <summary>Whether this account is a GM in game at all - i.e. carries any privileges.</summary>
		public bool IsGm => GmLevel is not null;

		/// <summary>Whether this account holds one privilege. False for a non-GM whatever the bit.</summary>
		/// <remarks>
		///     ⚠️ Pattern-matched rather than written `(GmLevel &amp; bit) != 0`: on a NULL level that lifts to
		///     `null != 0`, which is TRUE, so a non-GM would hold every privilege. Same shape as the client's own
		///     `GetStat` returning -1 for a stat it does not have (#902) - an absent value is not a zero.
		/// </remarks>
		public bool HasGmPrivilege(GmPrivileges privilege)
		{
			return GmLevel is { } level && (level & (uint)privilege) != 0;
		}

		/// <summary>
		///     Turns one privilege on or off. A no-op on a non-GM: flipping a bit must never PROMOTE an account, or
		///     `.godmode` on someone who is not a GM would quietly make them one.
		/// </summary>
		public void SetGmPrivilege(GmPrivileges privilege, bool on)
		{
			if (GmLevel is not { } level)
				return;

			GmLevel = on
				? level | (uint)privilege
				: level & ~(uint)privilege;
		}

		/// <summary>
		///     The value to put in character stat <c>0x0D7</c>. Zero for a non-GM, which is what stops the client
		///     entering GM mode at all.
		/// </summary>
		public uint ClientGmLevel()
		{
			return GmLevel ?? 0;
		}

		[MaxLength(128)]
		public string Password { get; set; } = null!;

		[MaxLength(128)]
		public string UserName { get; set; } = null!;

		// The login nonce (see docs/protocol/01-crypto.md): sent to the client at Authenticate, echoed back inside
		// the TEA block at AnswerChallenge, and compared there. ROTATED on every Authenticate (UniverseAgentListener)
		// - it was previously a constant empty string for every account, which meant a captured login block replayed
		// forever. Rendered on the wire as 32 lowercase hex chars ("N"), the shape retail used.
		public Guid AuthChallenge { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string? LastIpAddress { get; set; }

		public uint AuthStatus { get; set; }

		// The ACCOUNT-wide client settings archive (hotkeys.xml + the account Prefs_2.xml), stored exactly as the client
		// uploaded it with UpdateSubscriptionSettings (#611). Compressed, and deliberately opaque to the server - see
		// CharacterClientSettings for why it must not be re-encoded. Null until the client first uploads one.
		public byte[]? SubscriptionSettings { get; set; }

		// The id the client is currently using for this session: a real Character.Id during normal play, or
		// the temporary create-editor instance (0x021662C8) during character creation. Deliberately NOT a
		// foreign key - it must hold the temp instance without a constraint violation (so db.SaveChanges
		// works), and deleting a character must never cascade into deleting the account.
		public uint ClientInstance { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {UserName}";
		}
	}
}

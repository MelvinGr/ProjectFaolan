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
		public AccountType Type { get; set; }

		public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.UtcNow;
		public DateTimeOffset? LastConnection { get; set; }

		// Whether the account has UNSEEN claims - drives the "new claims available" glow on the client's claims button
		// (CoreClaimIIR sub 0x0009). Set true when a claim is added; cleared when the player opens the Claims window
		// (the list request). One flag per account, not per-claim. See GameServerListener.Claims.cs / #359.
		public bool ClaimsAvailable { get; set; }

		// Persisted GM toggles (#279). Unlike the live WorldEntity flags (which clear on relog AND on every map
		// change, since a playfield change is a fresh world connection), these survive on the account and are
		// re-applied to the player's entity on each world entry. Only ever set true by the GM .godmode/.invisible
		// commands (gated to AccountType >= GameMaster), so a normal account never carries them.
		public bool GmGodMode { get; set; }
		public bool GmInvisible { get; set; }

		// The client GM privilege MASK (stat 0xD7 / 215) this account gets, overriding the role its TYPE would map
		// to. NULL - the default - means "use the role", which is what every account had before the admin UI could
		// set bits individually; a value here is an explicit per-account mask.
		//
		// ⚠️ `GmPrivileges.GodMode` is NOT stored here even when the mask is explicit - it is taken from GmGodMode
		// above, so the client's invulnerability and the server's `.godmode` can never disagree. That split is #902:
		// with the bit set the client DISCARDS every server-driven health decrease (three gates in its stat setter,
		// one per vital), so an account carrying it while the server did not think it was in god mode took damage,
		// died, and watched a full health bar the whole time. `ClientGmLevel()` is the one place that assembles both.
		public uint? GmPrivileges { get; set; }

		/// <summary>
		///     The GM privilege mask to put on the wire: the explicit <see cref="GmPrivileges" /> when set, otherwise
		///     the role this account's <see cref="Type" /> maps to - and in both cases the
		///     <see cref="Enums.GmPrivileges.GodMode" /> bit strictly follows <see cref="GmGodMode" />.
		/// </summary>
		public uint ClientGmLevel()
		{
			var mask = GmPrivileges ?? Type.ToClientGmLevel();

			// A normal account must stay at zero: ANY non-zero value flips the retail client into GM mode.
			if (Type < AccountType.GameMaster)
				return 0;

			return GmGodMode
				? mask | (uint)Enums.GmPrivileges.GodMode
				: mask & ~(uint)Enums.GmPrivileges.GodMode;
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

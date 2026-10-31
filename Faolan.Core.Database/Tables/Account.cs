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

		// When a timed ban lifts itself. Null for an indefinite ban (`.ban`) and for an account that is not
		// banned at all; a time in the past means the ban has expired and the next login clears it.
		//
		// The lift is a property of the data rather than of a running process, which is the whole point: a ban that
		// timed by an in-process timer would survive a restart inside its window, and a restart could not shorten
		// it either.
		//
		// It is deliberately not a second source of truth for "is this account banned". `State` remains that; this
		// only says when the state should stop applying. A row with `State = Banned` and an elapsed `BanExpiresAt` is
		// still banned until something reads it - `IDatabaseRepository.ClearExpiredBan` at login is what makes it so,
		// and it is the one place that transition happens.
		public DateTimeOffset? BanExpiresAt { get; set; }

		// Whether the account has unseen claims - drives the "new claims available" glow on the client's claims button
		// (CoreClaimIIR sub 0x0009). Set true when a claim is added; cleared when the player opens the Claims window
		// (the list request). One flag per account, not per-claim. Served by ClaimService.
		public bool ClaimsAvailable { get; set; }

		// The account's GM privileges, and the only place they live: the retail client's `gm_level` mask (character
		// stat 0x0D7 / 215, `GmPrivileges`), or null for an account that is not a GM at all.
		//
		// One nullable column, and deliberately not a set of them: `GodMode` is bit 0x200 and `InvisibleToMonsters`
		// is bit 0x80, both already in the mask retail puts on the wire, so a separate boolean beside the mask would
		// be the same fact stored twice and free to drift from it.
		//
		// Null and 0 are the same thing on the wire and deliberately not distinguished: any non-zero value flips the
		// retail client into GM mode, so "a GM with no privileges" is not representable - an account with every bit
		// cleared simply is not a GM. `IsGm` is therefore `GmLevel is not null`, not a second column that could disagree.
		//
		// This is not `WebAccountType`. That is the web admin site's role and says nothing about the game; an account
		// can administer the site without being a GM in-world, and vice versa.
		public uint? GmLevel { get; set; }

		/// <summary>Whether this account is a GM in game at all - i.e. carries any privileges.</summary>
		public bool IsGm => GmLevel is not null;

		/// <summary>Whether this account holds one privilege. False for a non-GM whatever the bit.</summary>
		/// <remarks>
		///     Pattern-matched rather than written `(GmLevel &amp; bit) != 0`: on a null level that lifts to
		///     `null != 0`, which is true, so a non-GM would hold every privilege. Same shape as the client's own
		///     `GetStat` returning -1 for a stat it does not have - an absent value is not a zero.
		/// </remarks>
		public bool HasGmPrivilege(GmPrivileges privilege)
		{
			return GmLevel is { } level && (level & (uint)privilege) != 0;
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
		// the TEA block at AnswerChallenge, and compared there. Rotated on every Authenticate (UniverseAgentListener)
		// - a constant would let a captured login block replay forever. Rendered on the wire as 32 lowercase hex
		// chars ("N"), the shape retail uses.
		public Guid AuthChallenge { get; set; }

		/// <summary>
		///     The login session's cookie, the client's <c>uCookie</c>: issued by the UniverseAgent once a login is
		///     authenticated, and the only proof every later tier sees that it was. Null until the account's first login.
		/// </summary>
		/// <remarks>
		///     The client presents it with each tier's handshake (PlayerAgent <c>Authenticate</c> and
		///     <c>AuthenticateLoggedIn</c>, GameServer <c>Authenticate2</c>, the chat Hello, CsPlayerAgent
		///     <c>Authenticate</c>), each a new connection on a port of its own, and those tiers may run in another
		///     process; so it is a column, not process state. Retail issues a fresh value on every login, 31 bits wide
		///     (139 captured logins, 139 different values, none with the top bit set). Rotating it means one login per account: a
		///     second login leaves the first client's open connections alone, but that client's next handshake (a zone
		///     change, a relog) is refused.
		/// </remarks>
		public uint? LoginCookie { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string? LastIpAddress { get; set; }

		public uint AuthStatus { get; set; }

		// The account-wide client settings archive (hotkeys.xml + the account Prefs_2.xml), stored exactly as the client
		// uploaded it with UpdateSubscriptionSettings. Compressed, and deliberately opaque to the server - see
		// CharacterClientSettings for why it must not be re-encoded. Null until the client first uploads one.
		public byte[]? SubscriptionSettings { get; set; }

		// The id the client is currently using for this session: a real Character.Id during normal play, or
		// the temporary create-editor instance (0x021662C8) during character creation. Deliberately not a
		// foreign key - it must hold the temp instance without a constraint violation (so db.SaveChanges
		// works), and deleting a character must never cascade into deleting the account.
		public uint ClientInstance { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {UserName}";
		}
	}
}

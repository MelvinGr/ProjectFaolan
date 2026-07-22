using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	[Index(nameof(UserName), IsUnique = true)]
	public class Account : BaseEntity
	{
		[NotMapped]
		public ulong LongId => 0x0000271200000000u + Id; // As used by the client

		public virtual ICollection<AccountClaim> Claims { get; set; } = [];

		public virtual ICollection<Character> Characters { get; set; } = [];

		[ConstrainToEnumValues]
		public AccountState State { get; set; }

		[ConstrainToEnumValues]
		public AccountType Type { get; set; }

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

		public DateTimeOffset CreationDate { get; set; } = DateTimeOffset.UtcNow;
		public DateTimeOffset? LastConnection { get; set; }

		[MaxLength(128)]
		public string Password { get; set; } = null!;

		[MaxLength(128)]
		public string UserName { get; set; } = null!;

		[MaxLength(32)]
		public string? AuthChallenge { get; set; }

		[MaxLength(64)] // INET6_ADDRSTRLEN 46
		public string? LastIpAddress { get; set; }

		public uint AuthStatus { get; set; }

		// The id the client is currently using for this session: a real Character.Id during normal play, or
		// the temporary create-editor instance (0x021662C8) during character creation. Deliberately NOT a
		// foreign key - it must hold the temp instance without a constraint violation (so db.SaveChanges
		// works), and deleting a character must never cascade into deleting the account.
		[Obsolete("Use Session.Character.Id instead")]
		public uint ClientInstance { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {UserName}";
		}
	}
}

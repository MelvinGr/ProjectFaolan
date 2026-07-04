using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A player guild. Wire identity is ID32 [type 0x0000DEB7][instance = Id]; members are Characters with
	// GuildId set (rank lives on the Character). Protocol: Captures/scripts/decode_guild.py / the hoxxling
	// guild_invite_accepted capture.
	[Index(nameof(Name), IsUnique = true)]
	public class Guild : BaseEntity
	{
		// The ID32 type word for guilds on the wire (characters use 0x0000C350).
		public const uint WireType = 0x0000DEB7;

		public virtual ICollection<Character> Members { get; set; }

		// The guild government form. NOTE: this is NOT the 0x203F's first byte (that is the CoreGuild_c streaming
		// version, fixed at 3 - see GuildService). The actual government-form byte sits after guildId and is fixed
		// at 1 on the wire today, because GuildRenownBlock only encodes that form's body layout (the captured
		// "Sepulchra Invictus" guild was form 1). This column is retained for a future real government-form feature
		// and is not yet emitted. The client UI maps the value to a government label (#219, docs/guilds.md).
		public byte GovernmentForm { get; set; } = 1;

		public DateTimeOffset CreationDate { get; set; }

		[MaxLength(512)]
		public string Description { get; set; } = "";

		[MaxLength(4096)]
		public string MessageOfTheDay { get; set; } = "";

		[MaxLength(64)]
		public string Name { get; set; }

		public uint Renown { get; set; }

		// Guild bank, in tin (u64 on the wire; the wallet wire unit).
		public ulong Cash { get; set; }
	}
}

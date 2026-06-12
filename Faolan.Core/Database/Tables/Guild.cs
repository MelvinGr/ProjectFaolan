using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A player guild. Wire identity is ID32 [type 0x0000DEB7][instance = Id]; members are Characters with
	// GuildId set (rank lives on the Character). Protocol: Captures/decode_guild.py / the hoxxling
	// guild_invite_accepted capture.
	[Index(nameof(Name), IsUnique = true)]
	public class Guild : BaseEntity
	{
		// The ID32 type word for guilds on the wire (characters use 0x0000C350).
		public const uint WireType = 0x0000DEB7;

		public virtual ICollection<Character> Members { get; set; }

		public DateTimeOffset CreationDate { get; set; }

		[MaxLength(64)]
		public string Name { get; set; }

		[MaxLength(512)]
		public string Description { get; set; } = "";

		[MaxLength(4096)]
		public string MessageOfTheDay { get; set; } = "";

		// Retail "Sepulchra Invictus" carried governmentForm 3 in every 0x203F snapshot; the client UI maps the
		// value to a government label. Keep retail's default until the forms are mapped.
		public byte GovernmentForm { get; set; } = 3;

		// Guild bank, in copper (u64 on the wire).
		public ulong Cash { get; set; }

		public uint Renown { get; set; }

		public override string ToString()
		{
			return $"{base.ToString()}: {Name}";
		}
	}
}

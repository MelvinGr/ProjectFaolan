using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Database.Attributes;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// A player guild. Wire identity is ID32 [type 0x0000DEB7][instance = Id]; members are Characters with
	// GuildId set (rank lives on the Character). Protocol: tools/capture/decode_guild.py / the hoxxling
	// guild_invite_accepted capture.
	// Guild names are unique per realm, not globally: realm Crom and realm Fury may each have a "Knights".
	[Index(nameof(RealmId), nameof(Name), IsUnique = true)]
	public class Guild : BaseEntity
	{
		// The realm this guild belongs to. Pinned at creation from the founder's realm - not derived from
		// members (a just-created or emptied guild has no member to infer from). Every guild query is realm-scoped so
		// two realm processes on the shared DB never cross-read. Backfilled to the existing single realm on migration.
		public uint RealmId { get; set; }

		[ForeignKey(nameof(RealmId))]
		public virtual Realm Realm { get; set; } = null!;

		public virtual ICollection<Character> Members { get; set; } = [];

		public DateTimeOffset CreationDate { get; set; }

		// The guild government form. Note: this is not the 0x203F's first byte (that is the CoreGuild_c streaming
		// version, fixed at 3 - see GuildService). The actual government-form byte sits after guildId and is fixed
		// at 1 on the wire today, because GuildRenownBlock only encodes that form's body layout (the captured
		// "Sepulchra Invictus" guild was form 1). This column is retained for a future real government-form feature
		// and is not yet emitted. The client UI maps the value to a government label (docs/subsystems/guilds.md).
		// The governing form the founder picked, from CreateGuild's `i_nGoverningForm` byte. It decides the guild's
		// whole rank ladder - see GuildRankLadder - so it has to be stored rather than merely logged. The default is
		// Cimmerian (0), the first form of the client's own table.
		// Closed enum - the client's table has exactly these seven and no Unknown fillers - so it takes the check.
		[ConstrainToEnumValues]
		public GuildGoverningForm GovernmentForm { get; set; } = GuildGoverningForm.Cimmerian;

		[MaxLength(512)]
		public string Description { get; set; } = "";

		[MaxLength(4096)]
		public string MessageOfTheDay { get; set; } = "";

		[MaxLength(64)]
		public string Name { get; set; } = null!;

		// Guild level (0x2038 rankings column; 20 is retail max). Cosmetic until guild XP is a real feature.
		public uint Level { get; set; } = 1;

		public uint Renown { get; set; }

		// Guild bank, in tin (u64 on the wire; the wallet wire unit).
		public ulong Cash { get; set; }

		public ulong RenownArtistry { get; set; }

		public ulong RenownGlory { get; set; }

		// The rankings "Total" column: the sum of the three categories (not the separate `Renown` accumulator that
		// feeds the 0x203F renown block). Not persisted - derived on read.
		[NotMapped]
		public ulong RenownTotal => RenownValor + RenownGlory + RenownArtistry;

		// The three renown categories the guild-rankings window (0x2038 DispatchGuildRankingsUpdate) ranks by; the
		// window's "Total" tab is their sum (RenownTotal), verified across every guild in the hoxxling rankings
		// capture. Stored as u64 (the wire width). Zeroed for a fresh guild.
		public ulong RenownValor { get; set; }
	}
}

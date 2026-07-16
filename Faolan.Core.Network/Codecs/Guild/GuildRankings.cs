using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// Byte-exact codec for the 0x2038 DispatchGuildRankingsUpdate body - the global guild leaderboard behind the
	// client's "Guild Renown Rankings" window (tabs Total/Valor/Glory/Artistry). Format decoded from the hoxxling
	// guild_invite_accepted capture (Captures/scripts/decode_guild_rankings.py, GuildRankingsTests). Every list is
	// AoC-count-framed: a leading u32 word == (elementCount + 1) * 0x3F1 (the client's readers assert word % 0x3F1
	// == 0 and take count = word/0x3F1 - 1). Body:
	//   [u32 (guildCount+1)*0x3F1][ guild records... ]
	//   [u32 (4+1)*0x3F1][ 4 category sections ]
	//   [u32 nextUpdateUnix]
	//   record  = [u32 0x0000DEB7][u32 guildId][s16 name][u64 total][u64 valor][u64 glory][u64 artistry]
	//             [u32 members][u32 level]        (total == valor + glory + artistry, verified for every guild)
	//   section = [u32 categoryIndex 0..3][u32 (entryCount+1)*0x3F1][ entries... ]
	//   entry   = [u32 0x0000DEB7][u32 guildId][u64 renown][u32 prevRank]
	// A section's entries are ordered by renown desc, so the CURRENT rank is the entry's position; the stored word is
	// the guild's position in the PREVIOUS snapshot, driving the window's "Change" column (change = position -
	// prevRank). With no ranking history we emit the current position, so a fresh ranking shows no movement. Records
	// are sorted by total desc; each section lists the top guilds by that category's renown (desc), capped at
	// MaxRanked. A guild with zero renown in a category is absent from that section (so a brand-new guild simply
	// doesn't appear on any tab - which is why we no longer replay the captured retail leaderboard). All the framing
	// words are DERIVED from the counts - a 0x3F1-invalid word (e.g. the old verbatim 0 for an empty list) is exactly
	// what makes the client abort the dispatch and drop the CsPlayerAgent connection.
	public sealed class GuildRankings
	{
		// The CSPlayerInterface opcode this body is the payload of (0x2038).
		public const CSPlayerInterfaceOpcodes Opcode = CSPlayerInterfaceOpcodes.DispatchGuildRankingsUpdate;

		public const uint GuildClass = 0x0000DEB7; // the guild ID32 type word (Guild.WireType)
		public const uint ListMultiplier = ListFrame.Unit; // count-framed-list word == (count + 1) * 0x3F1
		public const int CategoryCount = 4; // Total(0), Valor(1), Glory(2), Artistry(3)
		public const int MaxRanked = 20; // retail lists the top 20 guilds per tab

		public uint NextUpdateUnix { get; set; }
		public List<Row> Records { get; set; } = [];

		// Sections[category] = the ranked entries for that tab (index 0..3 == Total/Valor/Glory/Artistry).
		public List<Ranked>[] Sections { get; set; } = [[], [], [], []];

		// One guild's catalog row. Total is derived (valor + glory + artistry) - the wire carries it explicitly but it
		// is always the sum, so we recompute rather than store a fourth field that could drift.
		public readonly record struct Row(uint Id, string Name, ulong Valor, ulong Glory, ulong Artistry, uint Members, uint Level)
		{
			public ulong Total => Valor + Glory + Artistry;
		}

		public readonly record struct Ranked(uint Id, ulong Renown, uint PrevRank);

		// The renown value a given category ranks by, for a record row.
		public static ulong CategoryRenown(Row row, int category) => category switch
		{
			0 => row.Total,
			1 => row.Valor,
			2 => row.Glory,
			3 => row.Artistry,
			_ => throw new ArgumentOutOfRangeException(nameof(category))
		};

		// Builds the rankings from the server's guilds. Records are sorted by total desc; each category section is the
		// top MaxRanked guilds with renown > 0 in that category (desc, 0-based rank). nextUpdateUnix drives the
		// window's "NEXT RANKINGS UPDATE" countdown (0 = an already-elapsed timer, which is what a stale retail
		// capture shows too).
		public static GuildRankings FromGuilds(IEnumerable<Row> guilds, uint nextUpdateUnix = 0)
		{
			var rows = guilds.OrderByDescending(g => g.Total).ThenBy(g => g.Id).ToList();
			var rankings = new GuildRankings { NextUpdateUnix = nextUpdateUnix, Records = rows };

			for (var category = 0; category < CategoryCount; category++)
			{
				rankings.Sections[category] = rows
					.Where(r => CategoryRenown(r, category) > 0)
					.OrderByDescending(r => CategoryRenown(r, category))
					.ThenBy(r => r.Id)
					.Take(MaxRanked)
					.Select((r, i) => new Ranked(r.Id, CategoryRenown(r, category), (uint)i))
					.ToList();
			}

			return rankings;
		}

		public byte[] Encode()
		{
			var s = new FaolanStream();

			s.WriteUInt32(ListWord(Records.Count));
			foreach (var r in Records)
			{
				s.WriteUInt32(GuildClass).WriteUInt32(r.Id).WriteString(r.Name);
				s.WriteUInt64(r.Total).WriteUInt64(r.Valor).WriteUInt64(r.Glory).WriteUInt64(r.Artistry);
				s.WriteUInt32(r.Members).WriteUInt32(r.Level);
			}

			s.WriteUInt32(ListWord(CategoryCount));
			for (var category = 0; category < CategoryCount; category++)
			{
				s.WriteUInt32(category).WriteUInt32(ListWord(Sections[category].Count));
				foreach (var e in Sections[category])
					s.WriteUInt32(GuildClass).WriteUInt32(e.Id).WriteUInt64(e.Renown).WriteUInt32(e.PrevRank);
			}

			s.WriteUInt32(NextUpdateUnix);
			return s.ToArray();
		}

		public static GuildRankings Decode(byte[] body)
		{
			var s = new FaolanStream(body);
			var rankings = new GuildRankings();

			var recordCount = ListCount(s.ReadUInt32());
			for (var i = 0; i < recordCount; i++)
			{
				RequireGuildClass(s.ReadUInt32());
				var id = s.ReadUInt32();
				var name = s.ReadString();
				var total = s.ReadUInt64();
				var valor = s.ReadUInt64();
				var glory = s.ReadUInt64();
				var artistry = s.ReadUInt64();
				var members = s.ReadUInt32();
				var level = s.ReadUInt32();
				if (valor + glory + artistry != total)
					throw new InvalidOperationException($"guild {id:X} total {total} != valor+glory+artistry {valor + glory + artistry}");
				rankings.Records.Add(new Row(id, name, valor, glory, artistry, members, level));
			}

			var sectionCount = ListCount(s.ReadUInt32());
			if (sectionCount != CategoryCount)
				throw new InvalidOperationException($"expected {CategoryCount} ranking sections, got {sectionCount}");

			for (var category = 0; category < CategoryCount; category++)
			{
				var index = s.ReadUInt32();
				if (index != category)
					throw new InvalidOperationException($"section {category}: unexpected index {index}");

				var entryCount = ListCount(s.ReadUInt32());
				var entries = new List<Ranked>(entryCount);
				for (var i = 0; i < entryCount; i++)
				{
					RequireGuildClass(s.ReadUInt32());
					entries.Add(new Ranked(s.ReadUInt32(), s.ReadUInt64(), s.ReadUInt32()));
				}

				rankings.Sections[category] = entries;
			}

			rankings.NextUpdateUnix = s.ReadUInt32();
			if (s.Position != s.Length)
				throw new InvalidOperationException($"rankings decoded {s.Position} of {s.Length} bytes");

			return rankings;
		}

		// The count-framed-list word for a given element count. The framing is protocol-wide, not a guild thing -
		// these stay as the guild codec's local spelling but delegate, so there is ONE implementation (ListFrame).
		public static uint ListWord(int count) => ListFrame.ForCount(count);

		// The element count encoded in a count-framed-list word (throws if the word isn't a 0x3F1 multiple - the same
		// check the client's readers make before aborting the dispatch).
		public static int ListCount(uint word) => ListFrame.CountOf(word);

		private static void RequireGuildClass(uint word)
		{
			if (word != GuildClass)
				throw new InvalidOperationException($"expected guild class {GuildClass:X8}, got {word:X8}");
		}
	}
}

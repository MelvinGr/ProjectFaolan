using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     The <c>DispatchGuildRankingsUpdate</c> body - the global guild leaderboard behind the client's "Guild Renown
	///     Rankings" window (tabs Total/Valor/Glory/Artistry).
	/// </summary>
	/// <remarks>
	///     Decoded from the hoxxling guild_invite_accepted capture (tools/capture/decode_guild_rankings.py,
	///     <c>GuildRankingsTests</c>). Every list is count-framed through <c>ListFrame</c>; the client's readers assert the
	///     word is a whole number of frame units and derive the count from it.
	///     <code>
	///     [list frame][ guild records... ]
	///     [list frame][ 4 category sections ]
	///     [u32 nextUpdateUnix]
	///     record  = [u32 guildEntityClass][u32 guildId][s16 name][u64 total][u64 valor][u64 glory][u64 artistry]
	///               [u32 members][u32 level]      (total == valor + glory + artistry, verified for every guild)
	///     section = [u32 categoryIndex 0..3][list frame][ entries... ]
	///     entry   = [u32 guildEntityClass][u32 guildId][u64 renown][u32 prevRank]
	///     </code>
	///     <para>
	///         A section's entries are ordered by renown descending, so the current rank is the entry's position; the
	///         stored word is the guild's position in the previous snapshot, driving the window's "Change" column
	///         (change = position - prevRank). With no ranking history the current position is emitted, so a fresh ranking
	///         shows no movement.
	///     </para>
	///     <para>
	///         Records are sorted by total descending; each section lists the top guilds by that category's renown, capped
	///         at <c>MaxRanked</c>. A guild with zero renown in a category is absent from that section, so a brand-new
	///         guild appears on no tab. All the framing words are derived from the counts: a word that is not a whole
	///         number of frame units is what makes the client abort the dispatch and drop the CsPlayerAgent connection.
	///     </para>
	/// </remarks>
	public static class GuildRankingsCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildRankingsUpdate;

		public static byte[] Build(GuildRankings value)
		{
			using var s = new FaolanStream();

			s.WriteUInt32(ListFrame.ForCount(value.Records.Count));
			foreach (var r in value.Records)
			{
				s.WriteUInt32(EntityClass.Guild).WriteUInt32(r.Id).WriteString(r.Name);
				s.WriteUInt64(r.Total).WriteUInt64(r.Valor).WriteUInt64(r.Glory).WriteUInt64(r.Artistry);
				s.WriteUInt32(r.Members).WriteUInt32(r.Level);
			}

			s.WriteUInt32(ListFrame.ForCount(GuildRankings.CategoryCount));
			for (var category = 0; category < GuildRankings.CategoryCount; category++)
			{
				s.WriteUInt32(category).WriteUInt32(ListFrame.ForCount(value.Sections[category].Count));
				foreach (var e in value.Sections[category])
					s.WriteUInt32(EntityClass.Guild).WriteUInt32(e.Id).WriteUInt64(e.Renown).WriteUInt32(e.PrevRank);
			}

			return s.WriteUInt32(value.NextUpdateUnix).ToArray();
		}

		public static GuildRankings Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildRankingsCodec));

			var recordCount = r.ReadListCount();
			var records = new List<GuildRankings.Row>(recordCount);
			for (var i = 0; i < recordCount; i++)
			{
				r.ExpectU32Be(EntityClass.Guild, "the guild entity class");
				var id = r.ReadU32Be();
				var name = r.ReadStringU16Length();
				var total = r.ReadU64Be();
				var valor = r.ReadU64Be();
				var glory = r.ReadU64Be();
				var artistry = r.ReadU64Be();
				var members = r.ReadU32Be();
				var level = r.ReadU32Be();
				if (valor + glory + artistry != total)
					throw new CodecParseException($"{nameof(GuildRankingsCodec)}: guild {id:X} total {total} != valor+glory+artistry {valor + glory + artistry}");

				records.Add(new GuildRankings.Row(id, name, valor, glory, artistry, members, level));
			}

			var sectionCount = r.ReadListCount();
			if (sectionCount != GuildRankings.CategoryCount)
				throw new CodecParseException($"{nameof(GuildRankingsCodec)}: expected {GuildRankings.CategoryCount} ranking sections, got {sectionCount}");

			var sections = new IReadOnlyList<GuildRankings.Ranked>[GuildRankings.CategoryCount];
			for (var category = 0; category < GuildRankings.CategoryCount; category++)
			{
				var index = r.ReadU32Be();
				if (index != category)
					throw new CodecParseException($"{nameof(GuildRankingsCodec)}: section {category} carries index {index}");

				var entryCount = r.ReadListCount();
				var entries = new List<GuildRankings.Ranked>(entryCount);
				for (var i = 0; i < entryCount; i++)
				{
					r.ExpectU32Be(EntityClass.Guild, "the guild entity class");
					entries.Add(new GuildRankings.Ranked(r.ReadU32Be(), r.ReadU64Be(), r.ReadU32Be()));
				}

				sections[category] = entries;
			}

			var nextUpdateUnix = r.ReadU32Be();
			r.EnsureAtEnd();

			return new GuildRankings(records, sections, nextUpdateUnix);
		}
	}

	// Sections[category] is the ranked entries for that tab (index 0..3 = Total/Valor/Glory/Artistry).
	public sealed record GuildRankings(IReadOnlyList<GuildRankings.Row> Records, IReadOnlyList<GuildRankings.Ranked>[] Sections, uint NextUpdateUnix)
	{
		public const int CategoryCount = 4; // Total(0), Valor(1), Glory(2), Artistry(3)
		public const int MaxRanked = 20; // retail lists the top 20 guilds per tab

		// The renown value a given category ranks by, for a record row.
		public static ulong CategoryRenown(Row row, int category)
		{
			return category switch
			{
				0 => row.Total,
				1 => row.Valor,
				2 => row.Glory,
				3 => row.Artistry,
				_ => throw new ArgumentOutOfRangeException(nameof(category))
			};
		}

		// Builds the rankings from the server's guilds. Records are sorted by total descending; each category section is
		// the top MaxRanked guilds with renown > 0 in that category, descending and 0-based. nextUpdateUnix drives the
		// window's "NEXT RANKINGS UPDATE" countdown; 0 is an already-elapsed timer, which is what a stale retail capture
		// shows too.
		public static GuildRankings FromGuilds(IEnumerable<Row> guilds, uint nextUpdateUnix = 0)
		{
			var rows = guilds.OrderByDescending(g => g.Total).ThenBy(g => g.Id).ToList();
			var sections = new IReadOnlyList<Ranked>[CategoryCount];
			for (var category = 0; category < CategoryCount; category++)
			{
				sections[category] = rows
					.Where(r => CategoryRenown(r, category) > 0)
					.OrderByDescending(r => CategoryRenown(r, category))
					.ThenBy(r => r.Id)
					.Take(MaxRanked)
					.Select((r, i) => new Ranked(r.Id, CategoryRenown(r, category), (uint)i))
					.ToList();
			}

			return new GuildRankings(rows, sections, nextUpdateUnix);
		}

		// One guild's catalog row. Total is derived (valor + glory + artistry): the wire carries it explicitly but it is
		// always the sum, so it is recomputed rather than stored as a fourth field that could drift.
		public readonly record struct Row(uint Id, string Name, ulong Valor, ulong Glory, ulong Artistry, uint Members, uint Level)
		{
			public ulong Total => Valor + Glory + Artistry;
		}

		public readonly record struct Ranked(uint Id, ulong Renown, uint PrevRank);
	}
}

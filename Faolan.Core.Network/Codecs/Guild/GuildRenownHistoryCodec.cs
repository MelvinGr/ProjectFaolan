using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildRenownHistoryUpdate</c> - the renown-over-time figures behind the guild window's renown graph,
	///     the answer to c2s <c>RequestRenownHistory</c>.
	/// </summary>
	/// <remarks>
	///     It is four fixed entries rather than a blob: the client's method signature names them
	///     <c>(currentEntry, lastEntry, highestEntry, averageEntry)</c> [V1: docs/re/aoc-client-rpc-methods.json], and the
	///     captured body is exactly 112 bytes = 4 x 28. Each entry is
	///     <c>[u64 rank][u64 total][u64 recent][u32 level]</c>; read as three u64 plus a u32 the numbers are coherent (a
	///     rank in the hundreds or thousands, a large running total, a smaller recent figure, a small trailing count),
	///     while read as seven u32 every other word is a zero, which is what gives the u64s away.
	///     <para>
	///         What each entry varies over matches the client's names, and the corpus splits three ways: the first entry
	///         moves between two logins of the same character in the same guild - the live "current" row; the second and
	///         fourth change with the guild (the corpus has two) and hold across that guild's captures; the third is
	///         byte-identical in every capture on both guilds (11228 / 2772084 / 318876 / 128), and a row two different
	///         guilds share is not either guild's own figure, so "highest" is realm-wide rather than per-guild.
	///     </para>
	///     <para>
	///         The field meanings inside an entry come from magnitude alone - the client names the four entries, not their
	///         columns. [V0: the guild renown history entry's three value columns]
	///     </para>
	/// </remarks>
	public static class GuildRenownHistoryCodec
	{
		// The four entries the client's signature names, in wire order.
		public const int EntryCount = 4;

		// One entry's width: three u64 plus a u32.
		public const int EntryLength = sizeof(ulong) * 3 + sizeof(uint);

		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildRenownHistoryUpdate;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(GuildRenownHistory value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value.Current);
			Write(s, value.Last);
			Write(s, value.Highest);
			Write(s, value.Average);

			return s.ToArray();
		}

		public static GuildRenownHistory Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildRenownHistoryCodec));
			var value = new GuildRenownHistory(Read(ref r), Read(ref r), Read(ref r), Read(ref r));
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildRenownHistoryEntry Read(ref SpanReader r)
		{
			return new GuildRenownHistoryEntry(r.ReadU64Be(), r.ReadU64Be(), r.ReadU64Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, GuildRenownHistoryEntry v)
		{
			s.WriteUInt64(v.Rank).WriteUInt64(v.Total).WriteUInt64(v.Recent).WriteUInt32(v.Level);
		}
	}

	/// <summary>One 28-byte row of the renown history.</summary>
	public sealed record GuildRenownHistoryEntry(ulong Rank, ulong Total, ulong Recent, uint Level)
	{
		// A guild with no history: the all-zero row.
		public static readonly GuildRenownHistoryEntry Empty = new GuildRenownHistoryEntry(0, 0, 0, 0);
	}

	/// <summary>The four entries, named by the client's own method signature.</summary>
	public sealed record GuildRenownHistory(GuildRenownHistoryEntry Current, GuildRenownHistoryEntry Last, GuildRenownHistoryEntry Highest, GuildRenownHistoryEntry Average)
	{
		public static readonly GuildRenownHistory Empty = new GuildRenownHistory(GuildRenownHistoryEntry.Empty, GuildRenownHistoryEntry.Empty, GuildRenownHistoryEntry.Empty,
			GuildRenownHistoryEntry.Empty);
	}
}

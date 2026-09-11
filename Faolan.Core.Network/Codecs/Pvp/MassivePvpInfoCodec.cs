using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     <c>DispatchMassivePvpInfoRequestReturn</c> - the Massive-PvP (Border Kingdom battlekeep) guild leaderboard,
	///     the answer to c2s <c>MassivePvpInfoRequest</c>. The client polls it on login and again on every guild
	///     member-list request.
	/// </summary>
	/// <remarks>
	///     The client's method signature names the three top-level fields <c>(i_nLastLoadTime, i_cList,
	///     i_nTimeRegionOffset)</c> [V1: docs/re/aoc-client-rpc-methods.json], and its reader
	///     (<c>CSPlayerInterfaceDispatch_t::DispatchMassivePvpInfoRequestReturn</c>, FUN_01349cc0 into the list reader
	///     FUN_0134f090) confirms the order: a u32, a count-framed list, a u32.
	///     <code>
	///     [u32 lastLoadTime][list frame]  { count x entry }  [u32 timeRegionOffset]
	///     entry = [u32 key][u32 rank][u32 areaId][u32 battleSeconds][u32 signupSeconds][u32 scoreA][u32 scoreB]
	///             [u32 lastBattleUnix][u32 guildId][u16 len][guildName][u16 len][otherName][u32 tail][u8 tailFlag]
	///     </code>
	///     <para>
	///         The list is a map rather than a vector: the client's loop reads a u32 key before each element
	///         (FUN_0048f900 at the top of the loop body, then FUN_006d5ca0 for the element), which is why every
	///         captured entry starts with its rank twice - once as the map key, once as the entry's own first column.
	///     </para>
	///     <para>
	///         The trailing string must be read as a length-prefixed string, not as padding. Reading the entry's tail
	///         as "[u16 len][name][7 zero bytes]" has the same byte count as <c>[u16 0][u32 0][u8 0]</c> and so parses
	///         122 of the 125 distinct retail bodies, breaking on the three where <c>otherName</c> is non-empty
	///         ("Fury (Dead)", "Yellow Lotus (RP)") and the entry is 17-19 bytes longer. The grammar here is derived
	///         against all 125 distinct bodies from all 120 dumps (147 packets), each parsing to exactly 8 entries with
	///         nothing left over. See docs/re/minigame-massivepvp-messages.md.
	///     </para>
	///     <para>
	///         Column semantics beyond the ones the reader names come from what varies: <c>areaId</c> takes three
	///         values (11040 / 12070 / 13050) in contiguous runs of 3/3/2 over the 8 rows, the three Border Kingdom
	///         battlekeep areas, with the board showing the top guilds per area; the battle and signup second columns
	///         are constant in every entry of every captured body (2 h and 15 min in seconds); <c>lastBattleUnix</c> is
	///         a plain unix timestamp, and <c>guildId</c> matches the guild ids the same captures carry in the guild
	///         messages; <c>otherName</c> and the <c>tail</c>/<c>tailFlag</c> pair vary together, the ten entries with
	///         a non-empty <c>otherName</c> being exactly the ten with a non-zero tail.
	///         [V0: the Massive-PvP board's otherName / tail columns]
	///     </para>
	///     <para>
	///         No Massive-PvP layer runs here, so the server sends <c>Empty</c>, the zero-guild board. That form has no
	///         capture oracle - every retail board carries 8 guilds - so its shape follows from the round-trip-verified
	///         grammar above.
	///     </para>
	/// </remarks>
	public static class MassivePvpInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchMassivePvpInfoRequestReturn;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(MassivePvpInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(value.LastLoadTime);
			s.WriteUInt32(ListFrame.ForCount(value.Entries.Count));
			foreach (var entry in value.Entries)
				Write(s, entry);

			s.WriteUInt32(value.TimeRegionOffset);

			return s.ToArray();
		}

		public static MassivePvpInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MassivePvpInfoCodec));
			var lastLoadTime = r.ReadU32Be();
			var count = r.ReadListCount();
			var entries = new List<MassivePvpBoardEntry>(count);
			for (var i = 0; i < count; i++)
				entries.Add(Read(ref r));

			var value = new MassivePvpInfo(lastLoadTime, entries, r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}

		internal static MassivePvpBoardEntry Read(ref SpanReader r)
		{
			// The map key ahead of the element. It repeats the entry's own Rank in every captured body, but is kept as
			// its own column rather than assumed, because it is a separate word on the wire.
			var key = r.ReadU32Be();

			return new MassivePvpBoardEntry(key, r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(),
				r.ReadStringU16Length(), r.ReadStringU16Length(), r.ReadU32Be(), r.ReadU8());
		}

		internal static void Write(FaolanStream s, MassivePvpBoardEntry v)
		{
			s.WriteUInt32(v.Key)
				.WriteUInt32(v.Rank)
				.WriteUInt32(v.AreaId)
				.WriteUInt32(v.BattleSeconds)
				.WriteUInt32(v.SignupSeconds)
				.WriteUInt32(v.ScoreA)
				.WriteUInt32(v.ScoreB)
				.WriteUInt32(v.LastBattleUnix)
				.WriteUInt32(v.GuildId)
				.WriteString(v.GuildName)
				.WriteString(v.OtherName)
				.WriteUInt32(v.Tail)
				.WriteByte(v.TailFlag);
		}
	}

	/// <summary>
	///     One row of the Massive-PvP board. <c>Key</c> is the map key the client reads ahead of the row; every captured
	///     row repeats its <c>Rank</c> there.
	/// </summary>
	public sealed record MassivePvpBoardEntry(uint Key, uint Rank, uint AreaId, uint BattleSeconds, uint SignupSeconds, uint ScoreA, uint ScoreB, uint LastBattleUnix,
		uint GuildId, string GuildName, string OtherName, uint Tail, byte TailFlag);

	/// <summary>The whole reply. <c>Empty</c> is the board a server with no Massive-PvP layer sends.</summary>
	public sealed record MassivePvpInfo(uint LastLoadTime, IReadOnlyList<MassivePvpBoardEntry> Entries, uint TimeRegionOffset)
	{
		// The trailing word is `count + 1` in every captured body (8 guilds gives 9), which is also what the empty
		// board's list frame divides to. The client names it i_nTimeRegionOffset, so the two readings are
		// indistinguishable on a corpus where every board has the same size; 1 satisfies both.
		public static readonly MassivePvpInfo Empty = new MassivePvpInfo(0, [], 1);
	}
}

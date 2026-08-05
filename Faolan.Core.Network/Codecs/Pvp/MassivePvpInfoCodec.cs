using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// 0x20A5 DispatchMassivePvpInfoRequestReturn - the Massive-PvP (Border Kingdom battlekeep) guild leaderboard,
	// the answer to c2s MassivePvpInfoRequest (0x2077, no arguments). The client polls it on login and again on
	// every guild member-list request.
	//
	// The client's own method signature names the three top-level fields `(i_nLastLoadTime, i_cList,
	// i_nTimeRegionOffset)` [V1: docs/re/aoc-client-rpc-methods.json], and its reader
	// (`CSPlayerInterfaceDispatch_t::DispatchMassivePvpInfoRequestReturn`, FUN_01349cc0 -> the list reader
	// FUN_0134f090) confirms the order: a u32, a 0x3F1-framed list, a u32.
	//
	//   [u32 lastLoadTime][u32 (count+1) * 0x3F1]  { count x entry }  [u32 timeRegionOffset]
	//
	// The LIST IS A MAP, not a vector: the client's loop reads a u32 KEY before each element (FUN_0048f900 at the
	// top of the loop body, then FUN_006d5ca0 for the element), which is why every captured entry starts with its
	// rank TWICE - once as the map key, once as the entry's own first column.
	//
	//   entry = [u32 key][u32 rank][u32 areaId][u32 7200][u32 900][u32 scoreA][u32 scoreB][u32 lastBattleUnix]
	//           [u32 guildId][u16 len][guildName][u16 len][otherName][u32 tail][u8 tailFlag]
	//
	// ⚠️ THE TRAILING STRING IS THE FIX THIS CODEC EXISTS FOR. The grammar this replaced ended an entry with
	// "[u16 len][name][7 zero bytes]", which is the same byte count as `[u16 0][u32 0][u8 0]` and therefore parses
	// 122 of the 125 distinct retail bodies. It breaks on the OTHER THREE, where `otherName` is non-empty
	// ("Fury (Dead)", "Yellow Lotus (RP)") and the entry is 17-19 bytes longer - the 0x3F1 trap's string cousin: a
	// length prefix that is zero in almost every capture reads as padding. Re-derived here against ALL 125 distinct
	// bodies from all 120 dumps (147 packets), every one of which now parses to exactly 8 entries with nothing left
	// over. See docs/re/minigame-massivepvp-messages.md.
	//
	// Column semantics beyond the ones the reader names are ours, from what varies:
	//   * `areaId` takes three values (11040 / 12070 / 13050) in contiguous runs of 3/3/2 over the 8 rows - the
	//     three Border Kingdom battlekeep areas, with the board showing the top guilds per area.
	//   * the 7200 / 900 columns are constant in every entry of every captured body (2h and 15min in seconds).
	//   * `lastBattleUnix` is a plain unix timestamp, and `guildId` matches the guild ids the same captures carry
	//     in the guild messages.
	//   * `otherName` and the `tail`/`tailFlag` pair vary TOGETHER: the ten entries with a non-empty `otherName`
	//     are exactly the ten with a non-zero tail. [V0: the Massive-PvP board's otherName / tail columns]
	//
	// WE RUN NO MASSIVE-PVP LAYER, so the server sends `Empty` - the honest zero-guild board. That is also the one
	// form with no capture oracle (every retail board carries 8 guilds), so its shape is inferred from the
	// round-trip-verified grammar above; it is confirmed in-client only in the negative sense that the CSPlayerAgent
	// route no longer drops (#408).
	public static class MassivePvpInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchMassivePvpInfoRequestReturn;

		// CANONICAL - the only method here that writes bytes.
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
			// The map key ahead of the element. It repeats the entry's own Rank in every captured body; kept as its
			// own column rather than assumed, because it is a separate word on the wire.
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

	// One row of the Massive-PvP board. `Key` is the map key the client reads ahead of the row; every captured row
	// repeats its `Rank` there.
	public sealed record MassivePvpBoardEntry(uint Key, uint Rank, uint AreaId, uint BattleSeconds, uint SignupSeconds, uint ScoreA, uint ScoreB, uint LastBattleUnix,
		uint GuildId, string GuildName, string OtherName, uint Tail, byte TailFlag);

	// The whole reply. `Empty` is the board a server with no Massive-PvP layer sends.
	public sealed record MassivePvpInfo(uint LastLoadTime, IReadOnlyList<MassivePvpBoardEntry> Entries, uint TimeRegionOffset)
	{
		// The trailing word is `count + 1` in every captured body (8 guilds -> 9), which is also what the empty
		// board's list frame divides to. The client names it i_nTimeRegionOffset, so the two readings are
		// indistinguishable on a corpus where every board has the same size; 1 satisfies both.
		public static readonly MassivePvpInfo Empty = new MassivePvpInfo(0, [], 1);
	}
}

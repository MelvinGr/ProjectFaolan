using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// 0x20AA DispatchBattleKeepInfoRequestReturnPhase3And4 - the s2c answer to c2s RequestBattleKeepInformation
	// (0x2042, CSPlayerAgent), the per-BATTLEKEEP status query that sits beside the guild-leaderboard board
	// (MassivePvpInfoCodec). The client opens the Border Kingdoms siege panel and queries one keep at a time.
	//
	// ⚠️ CAPTURED, and this refutes #147's standing "not one siege or city message occurs anywhere in the corpus".
	// `NecroL80_..._gameplay_annotated_9` carries four request/reply pairs, each immediately followed by a
	// DispatchGuildRankingsUpdate (0x2041) naming the owning guild - "Vendetta", "Hand of Stygia", "Fleurs de Lys"
	// - so the reply is the keep's numeric status and the owning-guild NAME arrives separately.
	//
	// Grammar (byte-exact on all four captured replies):
	//
	//   [u32 (count+1) * 0x3F1]  { count x entry }
	//   entry = [u32 keepId][u32 zoneWord][u32 owningGuildId][u32 0][u32 0][u32 timestampUnix][u32 state]
	//
	// Every captured reply carries exactly ONE entry (0x7E2). Field reading, in order of confidence:
	//   * keepId        - echoes the request's keep index (2 / 3 / 4).
	//   * zoneWord       - 0xDEB7 (57015) on every keep in the session; a per-zone constant (the Border Kingdoms
	//                     battlekeep zone), not the owning guild.
	//   * owningGuildId - CROSS-CORROBORATED three ways: keep 4's value 0x7B726 is "Vendetta"'s guild id and keep 2's
	//                     0x7C6A6 is "Hand of Stygia"'s, BOTH matching (a) the guildId column of the same session's
	//                     MassivePvp board (MassivePvpInfoCodec) and (b) the guild NAME in the DispatchGuildRankingsUpdate
	//                     (0x2041) that immediately follows each reply.
	//   * timestampUnix - a plain unix time in the capture's own window (0x6A88xxxx ~ 2026-08), and LATER than the
	//                     board's lastBattle time for the same guild - consistent with the keep's next-vulnerable
	//                     time. #147 establishes a vulnerability WINDOW. [V0: that this timestamp IS that window]
	//   * state         - 2 on every captured keep. The message is named "Phase3And4"; the Phase1And2 twin (0x20A9)
	//                     has a different, single-sample shape and is not decoded here. [V0: the state/phase meaning]
	//
	// The two zero words are carried as Word3/Word4 so a reply that fills them round-trips rather than being
	// rejected (the StatDamageIIR precedent). WE RUN NO SIEGE LAYER, so nothing BUILDS this in-world yet; the codec
	// exists so the decode is byte-exact and citable rather than prose. See docs/subsystems/guild-cities-and-sieges.md.
	public static class BattleKeepInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchBattleKeepInfoRequestReturnPhase3And4;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(BattleKeepInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Keeps.Count));
			foreach (var keep in value.Keeps)
				Write(s, keep);

			return s.ToArray();
		}

		public static BattleKeepInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(BattleKeepInfoCodec));
			var count = r.ReadListCount();
			var keeps = new List<BattleKeepInfoEntry>(count);
			for (var i = 0; i < count; i++)
				keeps.Add(Read(ref r));

			r.EnsureAtEnd();

			return new BattleKeepInfo(keeps);
		}

		internal static BattleKeepInfoEntry Read(ref SpanReader r)
		{
			return new BattleKeepInfoEntry(r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, BattleKeepInfoEntry keep)
		{
			s
				.WriteUInt32(keep.KeepId)
				.WriteUInt32(keep.ZoneWord)
				.WriteUInt32(keep.OwningGuildId)
				.WriteUInt32(keep.Word3)
				.WriteUInt32(keep.Word4)
				.WriteUInt32(keep.TimestampUnix)
				.WriteUInt32(keep.State);
		}
	}

	public sealed record BattleKeepInfo(IReadOnlyList<BattleKeepInfoEntry> Keeps);

	public sealed record BattleKeepInfoEntry(
		uint KeepId,
		uint ZoneWord,
		uint OwningGuildId,
		uint Word3,
		uint Word4,
		uint TimestampUnix,
		uint State);
}

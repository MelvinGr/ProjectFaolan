using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

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
	//   entry = [ID32 owner: u32 class][u32 guildId]  <- preceded by [u32 keepId]
	//         = [u32 keepId][u32 EntityClass.Guild][u32 owningGuildId][u32 attackerClass][u32 attackerGuildId]
	//           [u32 nextPhaseUnix][u32 phase]
	//
	// ⭐ The columns are NAMED FROM THE CLIENT'S OWN VIEW, not inferred from values (#147, 2026-08-25). The reader
	// `FUN_0134f280` scatters the seven words into a 0x1c struct in slot order [0][1][2][5][6][4][3], and the
	// battlekeep window's filler `FUN_01315130` (`Views/MassivePvP/MassivePvPBattleKeepView.xml`) reads that struct:
	//   * slots [1],[2] are passed as ONE ID32 to the guild-name lookup and rendered under
	//     `MassivePvP_BattleKeepView_DefendingGuild` - so the word this codec used to call "zoneWord" is the guild
	//     ID32's CLASS word (EntityClass.Guild = 0xDEB7, the same pair GuildRankingsCodec writes), not a zone;
	//   * slot [3] (wire word 7) is the PHASE: `FUN_01315780` names it from LDB category 10032
	//     (`FUN_0060f190(0x2730, phase, 0)`) under `MassivePvP_BattleKeepView_Phase` - see BattleKeepPhase;
	//   * slot [4] (wire word 6) is the time the NEXT phase begins: `localtime` -> weekday + hh:mm under
	//     `MassivePvP_BattleKeepView_NextPhase` (and, in phase War, a countdown);
	//   * slots [5],[6] (wire words 4-5) are a second ID32, compared against the null ID32 and shown as the
	//     ATTACKING guild (`MassivePvP_BattleKeepView_AttackingGuild` / `..._UnknownGuildName`). Zero on every
	//     captured keep = no war declared; when set its class word is EntityClass.Guild too.
	// The owner is CROSS-CORROBORATED three ways besides: keep 4's 0x7B726 is "Vendetta"'s guild id and keep 2's
	// 0x7C6A6 is "Hand of Stygia"'s, BOTH matching (a) the guildId column of the same session's MassivePvp board
	// (MassivePvpInfoCodec) and (b) the guild NAME in the DispatchGuildRankingsUpdate (0x2041) that follows each reply.
	//
	// The Phase1And2 twin (0x20A9) has NO captured body - an earlier note here called a frame in
	// `Strie_rogue-assassin_login` its single sample, but that frame is GameCharInterface 0x29
	// (DispatchReplyLootForCharacterTemplateClusterOk) on the GameServer stream; `0x20A9` is only the lossy wire
	// rendering. Its layout is recovered from the client reader (`FUN_013b1680`) in
	// docs/subsystems/guild-cities-and-sieges.md and is not built here: it carries a nested attacker-ranking list.
	//
	// The attacker pair is carried as two raw words so a reply that fills them round-trips rather than being
	// rejected (the StatDamageIIR precedent); the OWNER's class word is asserted because every captured keep carries
	// it and a keep without an owner has no meaning in this reply. WE RUN NO SIEGE LAYER, so nothing BUILDS this
	// in-world yet; the codec exists so the decode is byte-exact and citable rather than prose.
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
			var keepId = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Guild, "the owning guild's entity class");
			var owningGuildId = r.ReadU32Be();
			var attackerClass = r.ReadU32Be();
			var attackerGuildId = r.ReadU32Be();
			var nextPhaseUnix = r.ReadU32Be();
			var phase = (BattleKeepPhase)r.ReadU32Be();
			return new BattleKeepInfoEntry(keepId, owningGuildId, attackerClass, attackerGuildId, nextPhaseUnix, phase);
		}

		internal static void Write(FaolanStream s, BattleKeepInfoEntry keep)
		{
			s
				.WriteUInt32(keep.KeepId)
				.WriteUInt32(EntityClass.Guild)
				.WriteUInt32(keep.OwningGuildId)
				.WriteUInt32(keep.AttackingGuildClass)
				.WriteUInt32(keep.AttackingGuildId)
				.WriteUInt32(keep.NextPhaseUnix)
				.WriteUInt32((uint)keep.Phase);
		}
	}

	public sealed record BattleKeepInfo(IReadOnlyList<BattleKeepInfoEntry> Keeps);

	// One keep's status. AttackingGuildClass/AttackingGuildId are the attacker's ID32 as two raw words: [0][0] is the
	// client's null ID32 (no war declared - every captured keep), a real attacker carries [EntityClass.Guild][id].
	public sealed record BattleKeepInfoEntry(
		uint KeepId,
		uint OwningGuildId,
		uint AttackingGuildClass,
		uint AttackingGuildId,
		uint NextPhaseUnix,
		BattleKeepPhase Phase);

	// The battlekeep's siege phase, named by the client from LDB category 10032 (`--report ldb-search
	// --report-arg 10032:<letter>`). The two reply methods are named for the phases they answer in: the keep window
	// shows the attacker RANKING list (the Phase1And2 record's nested list) below phase 2 and the single attacking
	// guild from phase 2 on. Every captured keep is in SilenceBeforeTheStorm.
	public enum BattleKeepPhase : uint
	{
		WarDeclaration = 0,
		Skirmish = 1,
		SilenceBeforeTheStorm = 2,
		War = 3,
		Aftermath = 4,
	}
}

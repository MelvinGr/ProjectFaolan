using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// Builds the beach 0x737A6DF9 snapshot TAILS entirely FROM SCRATCH (BuildFromScratch) - no captured-tails
	// template: the generated quest portion + the game-static PFAccessWindow_ catalog (AccessWindowCatalog typed
	// entries) + the code stat skeleton (TailsStatBlock) with the live character's vitals/XP + learned spells patched
	// in. The prefix is built by SnapshotPrefix, the root by FullCharSnapshotBuilder. The former captured
	// BeachTailsTemplate.bin asset is retired from the runtime (#258) - it survives only as the byte-exact golden
	// oracle in Faolan.Tests (BeachTailsFromScratchTests proves BuildFromScratch reproduces it for the captured char).
	public static class SnapshotTailsBuilder
	{
		// GENERATES the quest PORTION of the tails (@0..1292 = Quest_c journal + @609 objective region + index) from a
		// quest's DB definition + its seeded snapshot-generation fields, with the player's live `instance` (#186). The
		// three regions are byte-exact for the beach starter (QuestcGenerationTests). Used by the LIVE production path
		// (RetailLoginReplay, unconditional with a graceful fallback) to splice the GENERATED quest region over the
		// captured tails - byte-identical for a fresh beach char (in-client verified), the first construct-from-DB step.
		public const int QuestPortionLength = 1292;

		// The quest region a character with NO snapshot quests carries. Byte-exact constant across all 20 create
		// captures: a freshly created character's snapshot has ZERO quest blocks (the starter quest is delivered LIVE
		// instead - QuestLogList + QuestFullUpdate - then rides the snapshot only on later logins). Words:
		//   version 0x18 ; empty Quest_c list 0x3F1 ; index tag 0x3F1 ; index marker (0+1)*0x3F1 ; count 0 ;
		//   terminal 0x000156E7 (constant). See Captures/scripts/decode_snapshot_quests.py + the *-create captures.
		private const uint EmptyQuestTerminal = 0x000156E7;

		// The captured instance baked into the static regions (Strie's); substituted for the live character's instance
		// so the per-session entity references in the tails address the logged-in player (as the burst path re-addresses).
		private const uint TemplateInstance = 0x02165EFF;

		// The game-static PFAccessWindow_ playfield catalog (the tails region @1292..7917) - the one tails region that
		// is reference data, not generated. Emitted from the TYPED AccessWindowCatalog (the 86 PfAccessWindowEntry
		// records, formerly the AccessWindowTable.bin blob, #258) so no captured byte-blob is shipped; byte-identical
		// to the captured region (BeachTailsFromScratchTests). Identical for every character.
		private static readonly byte[] AccessWindowTable = SnapshotTails.SerializeAccessWindowEntries(AccessWindowCatalog.Entries);

		// #255: the multi-quest journal portion from the player's held quests. One QuestcRecord per quest (journal
		// entries), but the objective (@609) region + the quest index belong to the PRIMARY (tracked) quest only -
		// other held quests just add their journal record + their questId to the index list (decoded from the
		// compeltefirstquest 2-quest snapshot: beach + Casilda). Quests are ordered primary-first then by id (the
		// captured order). Reproduces that capture's 2247-byte quest region byte-exact (BuildQuestPortionTests).
		private static readonly QuestTextSlot[] HeaderSlots = [QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.AreaTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		// The single-quest quest portion (golden-test oracle for the multi-quest BuildQuestPortion: for one held quest
		// the two are byte-identical - see BuildQuestPortionTests).
		public static byte[] BuildSingleQuestPortion(QuestDefinition def, uint state, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);

			var quests = new QuestcRegion { Records = { QuestcRecord.BuildSingle(def, state, def.SnapshotN, def.SnapshotFieldEc, def.SnapshotFieldF0, def.SnapshotFieldF8) } };
			var objectives = QuestObjectiveRegion.Build(def, instance);
			var goalSteps = (def.IndexGoalSteps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToArray();
			var index = QuestIndexRegion.Build([def.QuestId], goalSteps, def.IndexTableField1, def.IndexTableField2, def.IndexTableField3, def.QuestId, def.IndexTrailerWord);

			byte[] portion = [.. quests.ToArray(), .. objectives.ToArray(), .. index.ToArray()];
			if (portion.Length != QuestPortionLength)
				throw new InvalidOperationException($"generated quest portion is {portion.Length} B, expected {QuestPortionLength}");

			return portion;
		}

		public static byte[] BuildEmptyQuestPortion()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(QuestcRegion.Version); // 0x18
			w.WriteUInt32(QuestcRegion.Frame); // 0x3F1 - empty Quest_c list ((0+1)*0x3F1)
			w.WriteUInt32(QuestIndexRegion.IndexTag); // 0x3F1
			w.WriteUInt32(QuestcRegion.Frame); // 0x3F1 - empty index marker ((0+1)*0x3F1)
			w.WriteUInt32(0); // index count = 0
			w.WriteUInt32(EmptyQuestTerminal); // 0x000156E7
			return w.ToArray();
		}

		// Builds the WHOLE beach 0x737A6DF9 tails FROM SCRATCH - no captured-tails template (#186). Assembles the three
		// regions, each independently proven byte-exact: the generated quest portion (BuildQuestPortion, @0..1292),
		// the game-static access-window catalog (AccessWindowCatalog typed entries, @1292..7917), and the code stat
		// skeleton (TailsStatBlock.CreateFreshBlock, @7917..end) with the live character's vitals/XP patched in. Then
		// re-addresses the template instance + stamps the playfield token. For the captured beach
		// character + instance this reproduces BeachTailsTemplate.bin byte-for-byte (BeachTailsFromScratchTests), so the
		// template .bin is no longer needed to author a beach entry.
		// Single-quest convenience (golden tests + the single-quest path): delegates to the multi-quest form with just
		// the beach quest. BuildFromScratch([beach], beach) == BuildFromScratch(beach), so byte-identical.
		public static byte[] BuildFromScratch(QuestDefinition beachDef, Character character, uint instance, uint? playfieldToken = null, IReadOnlyList<uint> knownSpellIds = null)
		{
			ArgumentNullException.ThrowIfNull(beachDef);
			return BuildFromScratch([beachDef], beachDef, character, instance, playfieldToken, knownSpellIds);
		}

		// The player's full set of snapshot-eligible held quests (#255): the journal region carries every held
		// quest's record + objective region (primary owns the index's terminal). For a beach-only char this equals
		// the beach-only overload. `primary` is the tracked quest (the beach quest when held). `knownSpellIds` are the
		// character's learned spells - spliced into the stat block's known-list so they persist across relog + place on
		// the action bar (#74/#71); null/empty leaves the list empty (byte-identical to the captured beach tails).
		public static byte[] BuildFromScratch(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, Character character, uint instance, uint? playfieldToken = null, IReadOnlyList<uint> knownSpellIds = null)
		{
			ArgumentNullException.ThrowIfNull(quests);
			// `primary` may be null IFF the character holds no snapshot quests (the empty-region create case); when
			// quests are present BuildQuestPortion enforces a non-null primary.

			var quest = BuildQuestPortion(quests, primary, instance);

			var block = TailsStatBlock.CreateFreshBlock();

			// Derive the character's LEVEL (stat 0x36) into the sheet. The skeleton hardcodes 1 because its diff sample
			// was all level-1 chars, so the level slot was misclassified as "fixed" - without this the client reads
			// level 1 from the snapshot and blocks every level-gated spell/item regardless of the player's real level.
			if (character != null)
				block.SetStat((uint)CharacterStatId.Level, character.Level);

			if (character?.MaxStats != null)
			{
				block.CurrentXp = character.Experience;
				// Author the WHOLE per-class stat block (displayed + base/secondary slots) so it is internally
				// consistent (#4/#186), not just the displayed maxima. For the captured beach char (Strie, Assassin
				// 98/0/236, base 90/0/230) this reproduces the template's slots exactly, so the golden stays byte-exact.
				var v = character.MaxStats.SnapshotVitals(character.Class);
				block.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);

				// #214: a dead character logs in dead - zero current-health (HealthCurrent stat 0x1B) while leaving
				// the max/base side full, so the client shows the release window at world entry (decode_death_snapshot.py).
				if (character.IsDead)
					block.CurrentHp = 0;
			}

			// #74/#71: splice the character's learned spells into the stat block's known-list (no-op when empty).
			TailsStatBlock.InjectKnownSpells(block, KnownSpellList.FromSpellIds(knownSpellIds));

			var statBytes = TailsStatBlock.Serialize(block);

			byte[] tails = [.. quest, .. AccessWindowTable, .. statBytes];

			// The quest portion already carries `instance`; the static access-window + stat regions carry the template's
			// (Strie's) instance, so re-address those occurrences. (No-op on the quest portion - it has no TemplateInstance.)
			if (instance != TemplateInstance)
				ReplaceRawUInt32(tails, TemplateInstance, instance);
			if (playfieldToken.HasValue)
				WorldDespawn.TryReplacePlayfieldToken(tails, playfieldToken.Value);

			return tails;
		}

		public static byte[] BuildQuestPortion(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, uint instance)
		{
			ArgumentNullException.ThrowIfNull(quests);
			if (quests.Count == 0)
				return BuildEmptyQuestPortion(); // no held snapshot quests -> the capture-proven empty region

			ArgumentNullException.ThrowIfNull(primary);

			// Wire order (decoded from the compeltefirstquest/Strie 2-quest region, byte-exact): the Quest_c list
			// header (version + count), then for EACH held quest its journal record IMMEDIATELY followed by its own
			// objective (@609) region, then the quest index. (The objective region's lead word is per-quest
			// (goals+1)*0x3F1.) The index is owned by the primary (tracked) quest - its goal-steps/table/trailer - but
			// lists every held quest's id. Primary ordered first.
			var ordered = quests.OrderByDescending(q => q.QuestId == primary.QuestId).ThenBy(q => q.QuestId).ToList();

			using var w = new FaolanStream();
			w.WriteUInt32(QuestcRegion.Version);
			w.WriteUInt32(((uint)ordered.Count + 1) * QuestcRegion.Frame);
			foreach (var q in ordered)
			{
				Record(q).Write(w);
				w.WriteArray(QuestObjectiveRegion.Build(q, instance).ToArray());
			}

			// Quest index: a shared header (tag, marker, ALL held questIds, count, sentinel) then ONE per-quest block
			// (the quest's goal-id table: its magic name + tf1-3 + goal-steps + a trailer). The block trailers CHAIN -
			// each links to the NEXT held quest's areaId; the last block carries the terminal word (the primary's
			// IndexTrailerWord). Decoded byte-exact from the 2-quest capture (#255).
			w.WriteUInt32(QuestIndexRegion.IndexTag);
			w.WriteUInt32(((uint)ordered.Count + 1) * QuestcRegion.Frame);
			foreach (var q in ordered)
				w.WriteUInt32(q.QuestId);
			w.WriteUInt32((uint)ordered.Count);
			w.WriteUInt32(QuestIndexRegion.Sentinel);
			for (var i = 0; i < ordered.Count; i++)
			{
				var q = ordered[i];
				w.WriteUInt32(q.IndexMagic);
				w.WriteUInt32(q.IndexTableField1);
				w.WriteUInt32(q.IndexTableField2);
				w.WriteUInt32(q.IndexTableField3);
				var steps = (q.IndexGoalSteps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToArray();
				w.WriteUInt32((uint)steps.Length);
				for (var s = 0; s < steps.Length; s++)
				{
					w.WriteUInt32(steps[s]);
					w.WriteUInt32((uint)(s + 1));
				}

				w.WriteUInt32(q.QuestId);
				w.WriteUInt32(1);
				w.WriteUInt32(0);
				w.WriteUInt32(0);
				w.WriteUInt32(i + 1 < ordered.Count
					? ordered[i + 1].AreaId
					: primary.IndexTrailerWord);
			}

			return w.ToArray();

			static QuestcRecord Record(QuestDefinition q)
			{
				return QuestcRecord.BuildSingle(q, q.SnapshotState, q.SnapshotN, q.SnapshotFieldEc, q.SnapshotFieldF0, q.SnapshotFieldF8, q.SnapshotField28);
			}
		}

		// A quest can ride the snapshot journal region (BuildQuestPortion) only if its Quest_c scalars are seeded
		// (SnapshotState != 0) AND all 5 header text refs are present - else BuildSingle throws. Quests failing this
		// fall to the live RestorePlayerQuests path instead, so the two never deliver the same quest twice (#255).
		public static bool IsSnapshotEligible(QuestDefinition def)
		{
			if (def is not { SnapshotState: not 0 })
				return false;

			var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
			return HeaderSlots.All(slots.Contains);
		}

		// Replaces every big-endian uint32 occurrence of `find` with `replace` (the tails are raw records, not
		// protobuf, so entity references appear as raw 4-byte big-endian values).
		private static void ReplaceRawUInt32(byte[] data, uint find, uint replace)
		{
			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, find);
			for (var i = 0; i + 4 <= data.Length; i++)
			{
				if (data[i] == needle[0] && data[i + 1] == needle[1] && data[i + 2] == needle[2] && data[i + 3] == needle[3])
				{
					BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i, 4), replace);
					i += 3;
				}
			}
		}
	}
}

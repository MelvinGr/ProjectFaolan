using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.World;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// Builds the beach 0x737A6DF9 snapshot TAILS from a SERVER-OWNED template asset (BeachTailsTemplate.bin, the
	// content-fixed regions: the PFAccessWindow_ table, the fresh-character record skeleton, the format scaffolding)
	// plus the live character's GENERATED fields (level, vitals/XP). This decouples the snapshot tails from the
	// captured world-burst packet - the server now owns and assembles them - and is the first step of constructing
	// the snapshot from the ground up (the prefix is built by BeachSnapshotPrefix, the root by FullCharSnapshotBuilder).
	//
	// What is GENERATED today: Level, current HP/Mana/Stamina, current XP (the per-character stat fields the client
	// renders). What is still TEMPLATE (content-fixed or not-yet-decoded, see [[snapshot_tails_construction_scope]]):
	// the access-window table, the per-character name-format XML, and the UnknownBefore progression record block.
	// For a fresh beach character these are the same fixed shape, so the template is correct; growing them into
	// generated output is the remaining decode work.
	public static class BeachTailsBuilder
	{
		// The captured instance baked into the template (Strie's); substituted for the live character's instance so
		// the per-session entity references in the tails address the logged-in player (as the burst path re-addresses).
		private const uint TemplateInstance = 0x02165EFF;

		private static readonly byte[] Template = LoadTemplate();

		// The game-static PFAccessWindow_ playfield catalog (the tails region @1292..7917) - the one tails region that
		// is reference data, not generated. Carved out of the template so the tails can be assembled with NO captured
		// tails (BuildFromScratch). Identical for every character.
		private static readonly byte[] AccessWindowTable = LoadAsset("AccessWindowTable.bin");

		// True when the server-owned template is available (it always is once embedded); guards the caller's fallback.
		public static bool IsAvailable => Template.Length > 0;

		// Assembles the beach tails for a character: the template with the live level + vitals patched in. Byte-
		// identical to the captured beach tails when given the captured character's values (BeachSnapshotConstruction
		// / golden test).
		public static byte[] Build(Character character)
		{
			return Build(character, TemplateInstance);
		}

		// As Build(character, instance), and additionally stamps `playfieldToken` over the template's baked-in
		// (beach-capture) token - the marker-framed per-playfield id the client learns from this snapshot and the
		// despawn protocol scopes on (WorldDespawn). Required when the tails are assembled for any map other than
		// the template's own playfield.
		public static byte[] Build(Character character, uint instance, uint playfieldToken)
		{
			var bytes = Build(character, instance);
			WorldDespawn.TryReplacePlayfieldToken(bytes, playfieldToken);
			return bytes;
		}

		// As Build(character), but re-addresses the template's captured instance to `instance` (the live character's
		// client instance) in the assembled tails - the tails carry raw big-endian entity references.
		public static byte[] Build(Character character, uint instance)
		{
			var tails = SnapshotTails.Deserialize(Template);

			if (character != null)
			{
				tails.Level = character.Level;
				if (tails.HasKnownRepeatedStatTable && character.MaxStats != null)
				{
					tails.PostAccessBlock!.CurrentXp = character.Experience;
					tails.PostAccessBlock.CurrentHp = character.MaxStats.Health;
					tails.PostAccessBlock.CurrentMana = character.MaxStats.Mana;
					tails.PostAccessBlock.CurrentStamina = character.MaxStats.Stamina;
				}
			}

			var bytes = tails.ToArray();
			if (instance != TemplateInstance)
				ReplaceRawUInt32(bytes, TemplateInstance, instance);

			return bytes;
		}

		// GENERATES the quest PORTION of the tails (@0..1292 = Quest_c journal + @609 objective region + index) from a
		// quest's DB definition + its seeded snapshot-generation fields, with the player's live `instance` (#186). The
		// three regions are byte-exact for the beach starter (QuestcGenerationTests). Used by the LIVE production path
		// (RetailLoginReplay, unconditional with a graceful fallback) to splice the GENERATED quest region over the
		// captured tails - byte-identical for a fresh beach char (in-client verified), the first construct-from-DB step.
		public const int QuestPortionLength = 1292;

		public static byte[] BuildBeachQuestPortion(QuestDefinition def, uint state, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);

			var quests = new QuestcRegion
			{
				Records = { QuestcRecord.BuildSingle(def, state, def.SnapshotN, def.SnapshotFieldEc, def.SnapshotFieldF0, def.SnapshotFieldF8) }
			};
			var objectives = QuestObjectiveRegion.Build(def, instance);
			var goalSteps = (def.IndexGoalSteps ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToArray();
			var index = QuestIndexRegion.Build(
				[def.QuestId], goalSteps, def.IndexTableField1, def.IndexTableField2, def.IndexTableField3,
				def.QuestId, def.IndexTrailerWord);

			byte[] portion = [.. quests.ToArray(), .. objectives.ToArray(), .. index.ToArray()];
			if (portion.Length != QuestPortionLength)
				throw new InvalidOperationException($"generated quest portion is {portion.Length} B, expected {QuestPortionLength}");
			return portion;
		}

		// #255: the multi-quest journal portion from the player's held quests. One QuestcRecord per quest (journal
		// entries), but the objective (@609) region + the quest index belong to the PRIMARY (tracked) quest only -
		// other held quests just add their journal record + their questId to the index list (decoded from the
		// compeltefirstquest 2-quest snapshot: beach + Casilda). Quests are ordered primary-first then by id (the
		// captured order). Reproduces that capture's 2247-byte quest region byte-exact (BuildQuestPortionTests).
		private static readonly QuestTextSlot[] HeaderSlots =
			[QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.AreaTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

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

		public static byte[] BuildQuestPortion(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, uint instance)
		{
			ArgumentNullException.ThrowIfNull(quests);
			ArgumentNullException.ThrowIfNull(primary);

			// Wire order (decoded from the compeltefirstquest/Strie 2-quest region, byte-exact): the Quest_c list
			// header (version + count), then for EACH held quest its journal record IMMEDIATELY followed by its own
			// objective (@609) region, then the quest index. (The objective region's lead word is per-quest
			// (goals+1)*0x3F1.) The index is owned by the primary (tracked) quest - its goal-steps/table/trailer - but
			// lists every held quest's id. Primary ordered first.
			var ordered = quests.OrderByDescending(q => q.QuestId == primary.QuestId).ThenBy(q => q.QuestId).ToList();

			using var w = new Faolan.Core.Data.FaolanStream();
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
				w.WriteUInt32(i + 1 < ordered.Count ? ordered[i + 1].AreaId : primary.IndexTrailerWord);
			}

			return w.ToArray();

			static QuestcRecord Record(QuestDefinition q) =>
				QuestcRecord.BuildSingle(q, q.SnapshotState, q.SnapshotN, q.SnapshotFieldEc, q.SnapshotFieldF0, q.SnapshotFieldF8, q.SnapshotField28);
		}

		// Builds the WHOLE beach 0x737A6DF9 tails FROM SCRATCH - no captured-tails template (#186). Assembles the three
		// regions, each independently proven byte-exact: the generated quest portion (BuildBeachQuestPortion, @0..1292),
		// the game-static access-window catalog (the embedded AccessWindowTable asset, @1292..7917), and the code stat
		// skeleton (TailsStatBlock.CreateFreshBlock, @7917..end) with the live character's vitals/XP patched in. Then
		// re-addresses the template instance + stamps the playfield token, exactly as Build does. For the captured beach
		// character + instance this reproduces BeachTailsTemplate.bin byte-for-byte (BeachTailsFromScratchTests), so the
		// template .bin is no longer needed to author a beach entry.
		// Beach-only convenience (golden tests + the single-quest path): delegates to the multi-quest form with just
		// the beach quest. BuildQuestPortion([beach], beach) == BuildBeachQuestPortion, so byte-identical.
		public static byte[] BuildFromScratch(QuestDefinition beachDef, Character character, uint instance, uint? playfieldToken = null)
		{
			ArgumentNullException.ThrowIfNull(beachDef);
			return BuildFromScratch([beachDef], beachDef, character, instance, playfieldToken);
		}

		// The player's full set of snapshot-eligible held quests (#255): the journal region carries every held
		// quest's record + objective region (primary owns the index's terminal). For a beach-only char this equals
		// the beach-only overload. `primary` is the tracked quest (the beach quest when held).
		public static byte[] BuildFromScratch(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, Character character, uint instance, uint? playfieldToken = null)
		{
			ArgumentNullException.ThrowIfNull(quests);
			ArgumentNullException.ThrowIfNull(primary);

			var quest = BuildQuestPortion(quests, primary, instance);

			var block = TailsStatBlock.CreateFreshBlock();
			if (character?.MaxStats != null)
			{
				block.CurrentXp = character.Experience;
				// Author the WHOLE per-class stat block (displayed + base/secondary slots) so it is internally
				// consistent (#4/#186), not just the displayed maxima. For the captured beach char (Strie, Assassin
				// 98/0/236, base 90/0/230) this reproduces the template's slots exactly, so the golden stays byte-exact.
				var v = character.MaxStats.SnapshotVitals(character.Class);
				block.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);
			}
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

		private static byte[] LoadTemplate() => LoadAsset("BeachTailsTemplate.bin");

		private static byte[] LoadAsset(string fileName)
		{
			var assembly = Assembly.GetExecutingAssembly();
			var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.Ordinal));
			if (name == null)
				return [];

			using var stream = assembly.GetManifestResourceStream(name);
			if (stream == null)
				return [];

			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}
	}
}

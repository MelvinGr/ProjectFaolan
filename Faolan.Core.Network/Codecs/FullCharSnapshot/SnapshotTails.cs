using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// CONSTRUCTS the FullChar snapshot's tails from a character + its quests - the generation policy that sits
	// beside FullCharSnapshotSubClassCodec rather than on it (#530 rule 1: a codec exposes Build and Parse, and
	// what to build FROM is a different question with a different answer).
	//
	// The three regions are each independently proven byte-exact: the generated quest portion (@0..1292), the
	// game-static access-window catalog (AccessWindowCatalog typed entries, @1292..7917) and the stat block
	// (SubClassStatBlock, @7917..end) authored with the character's vitals/XP + learned spells. For the captured
	// beach character + instance the three together reproduce BeachTailsTemplate.bin byte-for-byte
	// (SnapshotTailsFromScratchTests), which is why that template .bin is no longer shipped (#186/#258).
	public static class SnapshotTails
	{
		// The quest portion's length for the beach starter, kept as the documented landmark for the region split.
		public const int QuestPortionLength = 1292;

		private static readonly QuestTextSlot[] HeaderSlots = [QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.AreaTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		// The quest region a character with NO snapshot quests carries. Byte-exact constant across all 20 create
		// captures: a freshly created character's snapshot has ZERO quest blocks (the starter quest is delivered
		// LIVE instead - QuestLogList + QuestFullUpdate - then rides the snapshot only on later logins). Words:
		//   version 0x18 ; empty Quest_c list ; empty completed-journal frame ; empty index marker ; count 0 ;
		//   terminal QuestIndexRegionCodec.IndexTerminalWord.
		// That is exactly what the three EMPTY regions encode to, which is why this needs no literals of its own.
		// See tools/capture/decode_snapshot_quests.py + the *-create captures.
		public static byte[] EmptyQuestPortion()
		{
			return QuestPortion([], null!, 0);
		}

		// Single-quest convenience (golden tests + the single-quest path): FromScratch([beach], beach) ==
		// FromScratch(beach), so byte-identical.
		public static FullCharSnapshotSubClass FromScratch(QuestDefinition beachDef, Character character, uint instance, uint? playfieldToken = null,
			IReadOnlyList<uint>? knownSpellIds = null, IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null)
		{
			ArgumentNullException.ThrowIfNull(beachDef);
			return FromScratch([beachDef], beachDef, character, instance, playfieldToken, knownSpellIds, statSheet);
		}

		// The player's full set of snapshot-eligible held quests (#255): the journal region carries every held
		// quest's record + objective region (primary owns the index's terminal). `primary` is the tracked quest (the
		// beach quest when held). `knownSpellIds` are the character's learned spells - built INTO the stat block's
		// known-list so they persist across relog + place on the action bar (#74/#71); null/empty leaves the list
		// empty (byte-identical to the captured beach tails).
		public static FullCharSnapshotSubClass FromScratch(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, Character character, uint instance, uint? playfieldToken = null,
			IReadOnlyList<uint>? knownSpellIds = null, IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null,
			IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>? completedQuests = null, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(quests);
			// `primary` may be null IFF the character holds no snapshot quests (the empty-region create case); when
			// quests are present QuestRegions enforces a non-null primary.

			var (journal, completed, index) = QuestRegions(quests, primary, instance, completedQuests, areas, areaCatalog);

			// #74/#71: the character's learned spells are built INTO the stat block's known-list (no-op when empty).
			var knownList = KnownSpellLists.FromSpellIds(knownSpellIds);

			// A character with NO stored sheet is the create path - it legitimately gets the baseline skeleton. But a
			// sheet of the WRONG SIZE is corruption, and silently falling back to baseline stats there would ship a
			// level-80 character with skeleton vitals and no visible error. Fail loudly instead.
			if (statSheet is { Count: > 0 } && statSheet.Count != SubClassStatBlock.SheetSize)
			{
				throw new InvalidOperationException($"Stat sheet has {statSheet.Count} pairs, expected exactly {SubClassStatBlock.SheetSize} " +
				                                    $"(StatList#1 {SubClassStatBlock.StatList1Size} + StatList#2 {SubClassStatBlock.StatList2Size}). " +
				                                    "The snapshot carries TWO 0x3F1-framed StatList_t and both are built from this sheet, so a partial " +
				                                    "one cannot be written - fix the character's CharacterStats rows rather than padding here.");
			}

			// The stat sheet has ONE author (#447): SnapshotStatSheet picks the baseline from provenance and then
			// applies the SAME override table either way, so a modelled stat reaches the wire for an imported
			// character and a freshly-created one alike. The overrides land on the SHEET because that is where a
			// sheet is authored - the block is what the sheet is BUILT into. (They could equally ride the block's
			// id-addressed setters now, as the vitals below do; when the block was a flat uint[] they could not,
			// because SetStat scanned the inline known-spell records too and a spell id equal to an override stat id
			// corrupted a record.)
			var sheet = SnapshotStatSheet.For(character, playfieldToken, statSheet);
			var block = SubClassStatBlock.CreateBlock(sheet, knownList);

			if (character != null)
			{
				// The character's per-class vitals (current AND max) so the sheet is internally consistent (#4/#186)
				// - for EVERY character, whatever its provenance, exactly like the stat-id overrides above.
				//
				// This used to be `if (!imported && ...)`, on the reasoning that "an imported character already
				// carries its real vitals in the sheet". It does - but frozen at capture time, so a captured
				// character rendered the same health, stamina, mana and attributes at level 10 and at level 40
				// (#473), which is the very staleness the Level override exists to prevent. And the guard protected
				// nothing: CaptureCharacterImporter seeds MaxStats FROM the captured stats, so the values that reach
				// the wire here are the captured ones anyway on the first login - they simply track the character
				// from then on.
				if (character.MaxStats != null)
				{
					var v = character.MaxStats.SnapshotVitals(character.Class);
					block.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);
				}

				// #214: a dead character logs in dead - zero current-health so the client shows the release window.
				// Unconditional: whichever baseline the sheet came from, it now carries a full-health pool.
				if (character.IsDead)
					block.CurrentHp = 0;
			}

			// The tails are FULLY CONSTRUCTED for the live session - no post-hoc byte-patch: the quest portion is
			// built with `instance`, the access-window + stat regions carry no per-session ids, and the playfield
			// token is authored into the stat block above (from the live zone). Nothing is opaque here, so both
			// carried-verbatim regions are empty.
			return new FullCharSnapshotSubClass
			{
				Quests = journal,
				CompletedQuests = completed,
				QuestIndex = index,
				UnknownBeforeAccessWindowTable = [],
				AccessWindowEntries = [.. AccessWindowCatalog.Entries],
				PostAccessBlock = block,
				UnknownAfterAccessWindowTable = []
			};
		}

		// A quest can ride the snapshot journal region only if its Quest_c scalars are seeded AND all 5 header text
		// refs are present. Quests failing this fall to the live RestorePlayerQuests path instead, so the two never
		// deliver the same quest twice (#255). The portion is Quest_c records + each quest's @609 objective region +
		// the quest INDEX, so a quest needs its record scalars, its five header text refs, its goals, AND its area.
		//
		// The index block's name word is the AREA's 4CC (29 of 29 captured blocks) and its id list is the area's
		// quest catalog - neither is per-quest. The gate here used to test QuestDefinition.IndexMagic, which models
		// that area-level word per quest and was therefore set on exactly two rows, so nothing passed.
		//
		// ⚠️ Getting this wrong is not a soft failure: an under-specified quest desyncs the client, which then dies
		// in StatList_t ("(nSize%1009)==0 && nSize>0, inconsistent IdentityList"). A round-trip through our own
		// codec will NOT catch that - reader and writer share the model - so widen this only with a byte-exact
		// regeneration test behind it.
		public static bool IsSnapshotEligible(QuestDefinition? def, QuestArea? area)
		{
			if (def is not { HasSnapshotRecord: true })
				return false;

			// Only quests whose objective region provably reproduces the wire (see
			// QuestDefinition.ObjectiveRegionVerified). The region has undecoded fields left, and an unreproducible
			// one takes the whole login down with it.
			if (def.Goals.Count == 0 || !def.ObjectiveRegionVerified || area is not { Magic: not 0 })
				return false;

			// ⚠️ TEMPORARILY OFF (#389). Everything above is satisfied by 9 of NecroL80's quests, but the @609
			// OBJECTIVE region we would generate for them is not trustworthy yet: regenerating the corpus reproduces
			// only 5 of 99 quests byte-exact (CapturedObjectiveRegions_RebuildFromTheDatabase_ByteExact - 570 differ
			// in length, 1030 differ at equal length). Shipping it asserts the client on statlist.cpp:134.
			//
			// The quest index and the completed journal DO regenerate byte-exact (23/23 and 81/81), so this is the
			// one region left. Flip objectiveGateGreen to true when the @609 objective region reproduces the wire -
			// not before: a snapshot the client rejects takes the whole login with it, and the completed journal
			// rides along with the active one.
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse
			var objectiveGateGreen = false;
			if (!objectiveGateGreen)
				return false;

			var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
			return HeaderSlots.All(slots.Contains);
		}

		// GENERATES the quest PORTION of the tails (@0..1292 = Quest_c journal + @609 objective region + index) from
		// a quest's DB definition + its seeded snapshot-generation fields, with the player's live `instance` (#186).
		// #255: the multi-quest journal portion from the player's held quests. One QuestcRecord per quest (journal
		// entries), but the objective (@609) region + the quest index belong to the PRIMARY (tracked) quest only -
		// other held quests just add their journal record + their questId to the index list (decoded from the
		// compeltefirstquest 2-quest snapshot: beach + Casilda). Quests are ordered primary-first then by id (the
		// captured order). Reproduces that capture's 2247-byte quest region byte-exact (BuildQuestPortionTests).
		public static byte[] QuestPortion(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, uint instance,
			IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>? completed = null, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			var (journal, completedRegion, index) = QuestRegions(quests, primary, instance, completed, areas, areaCatalog);
			return [.. QuestcRegionCodec.Build(journal), .. CompletedQuestJournalRegionCodec.Build(completedRegion), .. QuestIndexRegionCodec.Build(index)];
		}

		// The three typed regions that open the tails, in wire order (decoded from the compeltefirstquest/Strie
		// 2-quest region, byte-exact): the Quest_c journal - for EACH held quest its record IMMEDIATELY followed by
		// its own objective (@609) region - then the COMPLETED-quest journal, then the quest index. The index is
		// owned by the primary (tracked) quest - its goal-steps/table/trailer - but lists every held quest's id.
		// Primary ordered first.
		private static (QuestcRegion Journal, CompletedQuestJournalRegion Completed, QuestIndexRegion Index) QuestRegions(IReadOnlyList<QuestDefinition> quests,
			QuestDefinition primary, uint instance, IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>? completed,
			IReadOnlyDictionary<uint, QuestArea>? areas, IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog)
		{
			ArgumentNullException.ThrowIfNull(quests);

			// No ACTIVE snapshot quests -> the capture-proven EMPTY regions, and the completed journal is DROPPED
			// with them.
			//
			// ⚠️ That is deliberate. "Empty Quest_c list + completed journal" is a shape the corpus NEVER shows: all
			// 81 captured completed journals sit alongside a NON-EMPTY active journal (0 counter-examples in 104
			// captures). Emitting it anyway - which this did briefly - desynced the client and tripped
			// statlist.cpp:134 "inconsistent IdentityList", the same assert as #389.
			//
			// So the completed tab cannot ship before the ACTIVE journal carries quests: the two travel together.
			// Widening IsSnapshotEligible is the unblocking work (#389), not a change here.
			if (quests.Count == 0)
				return (new QuestcRegion(), CompletedQuestJournalRegions.ForAreas([]), new QuestIndexRegion());

			ArgumentNullException.ThrowIfNull(primary);

			var ordered = quests.OrderByDescending(q => q.QuestId == primary.QuestId).ThenBy(q => q.QuestId).ToList();

			var journal = new QuestcRegion();
			foreach (var q in ordered)
			{
				// The objective region is part of the RECORD (interleaved per quest), so it rides on the record and
				// QuestcRecordCodec.Write emits it - keeping the writer symmetric with QuestcRegionCodec.Read (#384).
				var record = QuestcRecords.ForQuest(q, q.Level);
				record.Objectives = QuestObjectiveRegions.ForQuest(q, instance);
				journal.Records.Add(record);
			}

			// The COMPLETED-quest journal sits between the active journal and the index (#390) and is ALWAYS present
			// - as its own frame word even when empty (ListFrame.ForCount(0) == 0x3F1). That empty frame is exactly
			// the 0x3F1 the index USED to swallow as a phantom "IndexTag" (#391); emitting it here (and dropping the
			// tag from QuestIndexRegion) keeps the beach/2-quest goldens byte-identical while making the model
			// correct.
			return (journal, CompletedQuestJournalRegions.ForAreas(completed ?? []), QuestIndexRegions.ForQuests(ordered, primary, areas, areaCatalog));
		}
	}
}

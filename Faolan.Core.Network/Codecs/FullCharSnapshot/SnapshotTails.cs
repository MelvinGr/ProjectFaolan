using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     Constructs the FullChar snapshot's tails from a character and its quests - the generation policy that sits
	///     beside <see cref="FullCharSnapshotSubClassCodec" /> rather than on it, since a codec exposes Build and Parse
	///     and what to build from is a different question.
	/// </summary>
	/// <remarks>
	///     The three regions are each independently proven byte-exact: the generated quest portion (@0..1288), the
	///     game-static access-window catalog (<c>AccessWindowCatalog</c> typed entries, @1288..7917, its list frame word
	///     included) and the stat block (<see cref="SubClassStatBlock" />, @7917..end) authored with the character's
	///     vitals, XP and learned spells. For the captured beach character and instance the three together reproduce
	///     BeachTailsTemplate.bin byte-for-byte (<c>SnapshotTailsFromScratchTests</c>).
	/// </remarks>
	public static class SnapshotTails
	{
		// The quest portion's length for the beach starter, kept as the documented landmark for the region split. 1288: the
		// four bytes past it are the access-window table's own list frame word.
		public const int QuestPortionLength = 1288;

		private static readonly QuestTextSlot[] HeaderSlots = [QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.QuestLineTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		// The quest region a character with no snapshot quests carries. Byte-exact constant across all 20 create captures: a
		// freshly created character's snapshot has zero quest blocks, the starter quest being delivered live instead
		// (QuestLogList plus QuestFullUpdate) and riding the snapshot only on later logins. The words are version 0x18, an
		// empty Quest_c list, an empty completed-journal frame, an empty index marker and count 0 - exactly what the three
		// empty regions encode to, which is why this needs no literals of its own. See
		// tools/capture/decode_snapshot_quests.py and the *-create captures.
		public static byte[] EmptyQuestPortion()
		{
			return QuestPortion([], null!, 0);
		}

		// Single-quest convenience (golden tests and the single-quest path): FromScratch([beach], beach) equals
		// FromScratch(beach), byte for byte.
		public static FullCharSnapshotSubClass FromScratch(QuestDefinition beachDef, Character character, CharacterMaxStats maxVitals, uint instance,
			uint? playfieldToken = null, IReadOnlyList<uint>? knownSpellIds = null, IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null)
		{
			ArgumentNullException.ThrowIfNull(beachDef);
			return FromScratch([beachDef], beachDef, character, maxVitals, instance, playfieldToken, knownSpellIds, statSheet);
		}

		// The player's full set of snapshot-eligible held quests: the journal region carries every held quest's record and
		// objective region, with the primary owning the index's terminal. `primary` is the tracked quest (the beach quest
		// when held). `knownSpellIds` are the character's learned spells, built into the stat block's known list so they
		// persist across a relog and place on the action bar; null or empty leaves the list empty, byte-identical to the
		// captured beach tails.
		//
		// `maxVitals` is the character's live ceiling - the world entity's EntityVitals, the only thing that knows the
		// character's gear. It is required rather than re-derived here because the class/level curve alone is not the number
		// the client draws, and a snapshot that disagrees with the vitals push that follows it shows the sheet flicking from
		// one max to the other. Ignored, and may be null, when `character` is null, which is the template and golden path.
		public static FullCharSnapshotSubClass FromScratch(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, Character character, CharacterMaxStats maxVitals, uint instance, uint? playfieldToken = null,
			IReadOnlyList<uint>? knownSpellIds = null, IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null,
			IReadOnlyList<(QuestLine Area, IReadOnlyList<QuestDefinition> Quests)>? completedQuests = null, IReadOnlyDictionary<uint, QuestLine>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null, Func<QuestGoalDefinition, uint>? solvedTimesOf = null, Func<uint, Item?>? rewardItemOf = null)
		{
			ArgumentNullException.ThrowIfNull(quests);
			// `primary` may be null only when the character holds no snapshot quests (the empty-region create case); when quests
			// are present QuestRegions enforces a non-null primary.

			// `solvedTimesOf` is the character's per-goal progress - see QuestPortion. Null builds an unstarted journal, which is
			// correct for the create path and for the goldens.
			var (journal, completed, index) = QuestRegions(quests, primary, instance, completedQuests, areas, areaCatalog, solvedTimesOf, rewardItemOf);

			// The character's learned spells are built into the stat block's known list (a no-op when empty).
			var knownList = KnownSpellLists.FromSpellIds(knownSpellIds);

			// A character with no stored sheet is the create path and legitimately gets the baseline skeleton. A sheet of the
			// wrong size is corruption, and silently falling back to baseline stats there would ship a level-80 character with
			// skeleton vitals and no visible error, so fail loudly instead.
			if (statSheet is { Count: > 0 } && statSheet.Count != SubClassStatBlock.SheetSize)
			{
				throw new InvalidOperationException($"Stat sheet has {statSheet.Count} pairs, expected exactly {SubClassStatBlock.SheetSize} " +
				                                    $"(StatList#1 {SubClassStatBlock.StatList1Size} + StatList#2 {SubClassStatBlock.StatList2Size}). " +
				                                    "The snapshot carries TWO 0x3F1-framed StatList_t and both are built from this sheet, so a partial " +
				                                    "one cannot be written - fix the character's CharacterStats rows rather than padding here.");
			}

			// The stat sheet has one author: SnapshotStatSheet picks the baseline from provenance and then applies the same
			// override table either way, so a modelled stat reaches the wire for an imported character and a freshly created one
			// alike. The overrides land on the sheet because that is where a sheet is authored - the block is what the sheet is
			// built into.
			var sheet = SnapshotStatSheet.For(character, playfieldToken, statSheet);
			var block = SubClassStatBlock.CreateBlock(sheet, knownList);

			if (character != null)
			{
				// The character's per-class vitals, current and max, so the sheet is internally consistent - for every character,
				// whatever its provenance, exactly like the stat-id overrides above. An imported character's captured sheet carries
				// its vitals frozen at capture time, so without this a captured character renders the same health, stamina, mana and
				// attributes at level 10 and at level 40.
				ArgumentNullException.ThrowIfNull(maxVitals);
				var v = maxVitals.SnapshotVitals(character.Class, character.Level);
				block.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);

				// A dead character logs in dead: zero current health, so the client shows the release window. Unconditional, since
				// whichever baseline the sheet came from now carries a full-health pool.
				if (character.IsDead)
					block.CurrentHp = 0;
			}

			// The tails are fully constructed for the live session, with no post-hoc byte patch: the quest portion is built with
			// `instance`, the access-window and stat regions carry no per-session ids, and the playfield token is authored into
			// the stat block above from the live zone.
			return new FullCharSnapshotSubClass
			{
				Quests = journal,
				CompletedQuests = completed,
				QuestIndex = index,
				AccessWindowEntries = [.. AccessWindowCatalog.Entries],
				PostAccessBlock = block
			};
		}

		// A quest can ride the snapshot journal region only if its Quest_c scalars are seeded and all five header text refs
		// are present. Quests failing this fall to the live restore path instead, so the two never deliver the same quest
		// twice. The portion is Quest_c records plus each quest's @609 objective region plus the quest index, so a quest
		// needs its record scalars, its five header text refs, its goals and its area.
		//
		// The index block's name word is the area's 4CC (29 of 29 captured blocks) and its id list is the area's quest
		// catalog; neither is per-quest.
		//
		// Getting this wrong is not a soft failure: an under-specified quest desyncs the client, which then dies in
		// StatList_t ("(nSize%1009)==0 && nSize>0, inconsistent IdentityList"). A round-trip through this codec will not
		// catch that, reader and writer sharing the model, so widen this only with a byte-exact regeneration test behind it.
		//
		// `lineSteps` is how many quests the quest's line has (its catalog length): the index block states the held quest's
		// step number against it, so the gate has to know whether there is an order to state.
		public static bool IsSnapshotEligible(QuestDefinition? def, QuestLine? area, int lineSteps)
		{
			if (def is not { HasSnapshotRecord: true })
				return false;

			// The index block carries the held quest's step number in its line and the steps after it - the client draws them as
			// the journal's chapter numerals (QuestIndexRegion.Block.StepNumber). That number is the quest's JournalSequence,
			// which is capture-sourced and 0 on the 1,009 lines no capture has ordered. A multi-step line with no order would go
			// out as "step 0, all n to come", a block no capture shows, so the quest waits for its line's order the way it waits
			// for its line's 4CC. A single-step line has nothing to order and its one quest is step 1 by construction.
			// Open work tracked in Gitea #926.
			if (lineSteps > 1 && def.JournalSequence == 0)
				return false;

			// Only quests whose objective region provably reproduces the wire (see QuestDefinition.ObjectiveRegionVerified). The
			// region has undecoded fields left, and an unreproducible one takes the whole login down with it.
			if (def.Goals.Count == 0 || area is not { Magic: not 0 })
				return false;

			// Verified against a capture, or buildable from the goal rows. The two are not the same claim and the flag keeps
			// meaning only the first: "verified" is "reproduces a captured region byte-exact", and only 103 of 5,049 quests were
			// ever captured in a snapshot, so verification can never speak for the rest. A quest failing this is delivered
			// nowhere - active in CharacterQuests and never mentioned to the client, which reads its line as finished.
			if (!def.ObjectiveRegionVerified && !IsObjectiveRegionBuildable(def))
				return false;

			// The per-quest ObjectiveRegionVerified flag is what gates this, and the regeneration gate behind it currently
			// reproduces 99 of 99 captured objective regions byte-exact
			// (CapturedObjectiveRegions_RebuildFromTheDatabase_ByteExact). Two things keep it honest, both in the importer: the
			// verification compares like with like (retail has more than one spelling of a region and picks by the record's
			// XpReward - see ObjectiveGoalExtractor.CapturedRegions), and it runs after quest-wiring re-applies the
			// hand-authored goal fields the harvest clobbers, so it judges the rows actually shipped rather than an intermediate
			// state.
			//
			// The hazard is real: a region the client cannot parse takes the whole login down (statlist.cpp:134), and the
			// completed journal rides along with the active one. Widen what gets flagged only behind a byte-exact regeneration
			// gate, and never by flipping a quest's flag by hand.
			//
			// The region is not where the ceiling is (measured by `--report snapshot-eligibility`): 114 of 5,049 quests pass this
			// gate, and the region arm is one of five that all sit at exactly 114 - so is the `Level` word, so are the `Quest_c`
			// record scalars, and so is the summary header ref below. The report's second table asks how many quests fail exactly
			// one arm and the answer is zero on every arm: a captured journal carries the summary key, the level and each goal's
			// kind tag together, so the capture-only inputs are all known for the same quests, and relaxing any single arm widens
			// nothing. The scarcest input cannot be derived at all: the summary's category (50302) and RefId (the quest's own id)
			// are mechanical, but its per-string 128-bit LDB key is uniformly random, absent from the client and not a function
			// of (RefId, Category) - see Faolan.Core/Crypt/LdbKey.cs. Without it the client cannot decrypt the text.
			// Open work tracked in Gitea #389.
			var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
			return HeaderSlots.All(slots.Contains);
		}

		// Whether a quest's @609 objective region can be built from its goal rows - as distinct from having been verified
		// against a capture.
		//
		// The discriminating field is the goal's kind tag. `QuestObjectiveRegions.ForGoal` writes it as the record's `K` and
		// falls back to 0 when the row has none, and a goal with no captured kind cannot be built into a region the client
		// will accept. Everything else the record needs is either structural (the [1] lead, the goal-id echo), echoed from
		// the definition, or legitimately zero on an unstarted goal, so the kind is the whole test - and it is a
		// wire-harvested field, observed rather than inferred.
		//
		// Partial goals are excluded here for the same reason ForQuest leaves them out of the region.
		//
		// This admits ~10 quests (113 carry a kind on every goal; 103 of those are already verified), all of them early
		// Tortage chain content. It is deliberately not a licence to ship any quest with goals: a region the client cannot
		// parse takes the whole login down (statlist.cpp:134), so the bar stays "every goal states its kind on the wire"
		// rather than "the builder produced some bytes".
		public static bool IsObjectiveRegionBuildable(QuestDefinition? def)
		{
			if (def == null)
				return false;

			var live = def.Goals.Where(g => !g.Partial).ToList();
			return live.Count > 0 && live.TrueForAll(g => g.Separator != null);
		}

		// Generates the quest portion of the tails (@0..1288 = Quest_c journal plus @609 objective region plus index) from a
		// quest's DB definition and its seeded snapshot-generation fields, with the player's live `instance`.
		//
		// The multi-quest journal portion comes from the player's held quests: one QuestcRecord per quest (the journal
		// entries), but the objective (@609) region and the quest index belong to the primary (tracked) quest only, other
		// held quests adding just their journal record and their questId to the index list. Decoded from the
		// compeltefirstquest two-quest snapshot (beach plus Casilda). Quests are ordered primary-first then by id, the
		// captured order, and this reproduces that capture's 2247-byte quest region byte-exact (BuildQuestPortionTests).
		//
		// `solvedTimesOf` is the player's progress on a goal - `QuestGoal_c::SolvedTimes`, the goal record's first reserved
		// word. Omitting it builds an unstarted journal (every goal at 0), which is what the goldens and a fresh character
		// carry; the live snapshot passes the character's own counters so the journal opens showing "3 of 7" rather than
		// resetting to zero on every login.
		public static byte[] QuestPortion(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, uint instance,
			IReadOnlyList<(QuestLine Area, IReadOnlyList<QuestDefinition> Quests)>? completed = null, IReadOnlyDictionary<uint, QuestLine>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null, Func<QuestGoalDefinition, uint>? solvedTimesOf = null,
			Func<uint, Item?>? rewardItemOf = null)
		{
			var (journal, completedRegion, index) = QuestRegions(quests, primary, instance, completed, areas, areaCatalog, solvedTimesOf, rewardItemOf);
			return [.. QuestcRegionCodec.Build(journal), .. CompletedQuestJournalRegionCodec.Build(completedRegion), .. QuestIndexRegionCodec.Build(index)];
		}

		// The three typed regions that open the tails, in wire order (decoded from the compeltefirstquest/Strie two-quest
		// region, byte-exact): the Quest_c journal - for each held quest its record immediately followed by its own objective
		// (@609) region - then the completed-quest journal, then the quest index. The index is owned by the primary (tracked)
		// quest, carrying its goal-steps, table and trailer, but lists every held quest's id. Primary is ordered first.
		private static (QuestcRegion Journal, CompletedQuestJournalRegion Completed, QuestIndexRegion Index) QuestRegions(IReadOnlyList<QuestDefinition> quests,
			QuestDefinition primary, uint instance, IReadOnlyList<(QuestLine Area, IReadOnlyList<QuestDefinition> Quests)>? completed,
			IReadOnlyDictionary<uint, QuestLine>? areas, IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog,
			Func<QuestGoalDefinition, uint>? solvedTimesOf = null, Func<uint, Item?>? rewardItemOf = null)
		{
			ArgumentNullException.ThrowIfNull(quests);

			// No active snapshot quests gives the capture-proven empty regions, and the completed journal is dropped with them.
			//
			// That is deliberate. "Empty Quest_c list plus completed journal" is a shape the corpus never shows: all 81 captured
			// completed journals sit alongside a non-empty active journal, with no counter-examples in 104 captures. Emitting it
			// anyway desyncs the client and trips statlist.cpp:134 "inconsistent IdentityList".
			//
			// The completed tab therefore cannot ship before the active journal carries quests: the two travel together, and
			// widening IsSnapshotEligible is the unblocking work. Open work tracked in Gitea #389.
			if (quests.Count == 0)
				return (new QuestcRegion(), CompletedQuestJournalRegions.ForAreas([]), new QuestIndexRegion());

			ArgumentNullException.ThrowIfNull(primary);

			var ordered = quests.OrderByDescending(q => q.QuestId == primary.QuestId).ThenBy(q => q.QuestId).ToList();

			var journal = new QuestcRegion();
			foreach (var q in ordered)
			{
				// The objective region is part of the record, interleaved per quest, so it rides on the record and
				// QuestcRecordCodec.Write emits it - keeping the writer symmetric with QuestcRegionCodec.Read. The four reward lists
				// are the quest's own set from its rows plus the line's from the catalog, built only when the caller can resolve
				// items, since the key each element carries comes off the Item row.
				var rewards = rewardItemOf == null
					? null
					: QuestRewardLists.ForQuest(q, areas?.GetValueOrDefault(q.QuestLineId) ?? q.Line, instance, rewardItemOf);
				var record = QuestcRecords.ForQuest(q, q.Level, rewards);
				record.Objectives = QuestObjectiveRegions.ForQuest(q, instance, solvedTimesOf);
				journal.Records.Add(record);
			}

			// The completed-quest journal sits between the active journal and the index and is always present, as its own frame
			// word even when empty. That empty frame is what an index reader can mistake for a phantom index tag; emitting it
			// here, and keeping the tag off QuestIndexRegion, is what makes the model correct while leaving the beach and
			// two-quest goldens byte-identical.
			return (journal, CompletedQuestJournalRegions.ForAreas(completed ?? []), QuestIndexRegions.ForQuests(ordered, primary, areas, areaCatalog));
		}
	}
}

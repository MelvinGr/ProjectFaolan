using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	public sealed class FullCharSnapshotSubClass : ICodec<FullCharSnapshotSubClass>
	{
		// ---- FROM-SCRATCH construction (formerly the separate SnapshotTailsBuilder class) ----
		// Builds the beach 0x737A6DF9 tails from a character + its held quests with NO captured template:
		// the generated quest portion + the typed PFAccessWindow_ catalog (AccessWindowCatalog) + the code
		// stat block (SubClassStatBlock) with the live vitals/XP/known-spells. BeachTailsFromScratchTests proves
		// it reproduces the retired BeachTailsTemplate.bin byte-for-byte (#186/#258).
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
		//   terminal 0x000156E7 (constant). See tools/capture/decode_snapshot_quests.py + the *-create captures.
		private const uint EmptyQuestTerminal = 0x000156E7;

		// The game-static PFAccessWindow_ playfield catalog (the tails region @1292..7917) - the one tails region that
		// is reference data, not generated. Emitted from the TYPED AccessWindowCatalog (the 86 PfAccessWindowEntry
		// records, formerly the AccessWindowTable.bin blob, #258) so no captured byte-blob is shipped; byte-identical
		// to the captured region (BeachTailsFromScratchTests). Identical for every character.
		private static readonly byte[] AccessWindowTable = SerializeAccessWindowEntries(AccessWindowCatalog.Entries);

		// #255: the multi-quest journal portion from the player's held quests. One QuestcRecord per quest (journal
		// entries), but the objective (@609) region + the quest index belong to the PRIMARY (tracked) quest only -
		// other held quests just add their journal record + their questId to the index list (decoded from the
		// compeltefirstquest 2-quest snapshot: beach + Casilda). Quests are ordered primary-first then by id (the
		// captured order). Reproduces that capture's 2247-byte quest region byte-exact (BuildQuestPortionTests).
		private static readonly QuestTextSlot[] HeaderSlots = [QuestTextSlot.Title, QuestTextSlot.Summary, QuestTextSlot.AreaTitle, QuestTextSlot.RegionName, QuestTextSlot.MapName];

		[JsonIgnore]
		public bool HasKnownRepeatedStatTable => PostAccessBlock?.LargestAdjacentRepeatedRun != null;

		[JsonIgnore]
		public byte[] OriginalData { get; private init; } = [];

		public byte[] UnknownAfterAccessWindowTable { get; init; } = null!;

		// Everything between the @609 objective region and the access-window table (the quest INDEX + any tail);
		// still opaque (decoded by tools/capture/decode_snapshot_quests.py).
		public byte[] UnknownBeforeAccessWindowTable { get; init; } = null!;

		// The COMPLETED-quest journal, grouped by area - the Completed tab's data, sitting directly after the active
		// journal (#390). Null for tails without one: the beach template holds no completed quests, which is exactly
		// why the region went unmodelled and every real journal's bytes fell into UnknownBefore.
		public CompletedQuestJournalRegion? CompletedQuests { get; init; }

		public int KnownHeaderLength { get; init; }

		public List<PfAccessWindowEntry> AccessWindowEntries { get; init; } = null!;

		// The quest JOURNAL region at the START of the tails (version + 0x3F1-framed Quest_c list), modeled from
		// the recovered FullPlayerCharacterIIR grammar (docs/re/fullplayer-snapshot-reader.md) instead of an opaque
		// header+string blob. Replaces the former Header0/Unknown*/RemoteFormat* fields, which were really the
		// first Quest_c record's header + its 5 <remoteformat> strings.
		public QuestcRegion Quests { get; init; } = new QuestcRegion();

		// The quest INDEX region (the [0x3F1] index + the "W6QT" goal-id table) between the @609 objective region and
		// the access-window table; modeled by QuestIndexRegion. Null when not present (then it rides UnknownBefore).
		public QuestIndexRegion? QuestIndex { get; init; }

		public SubClassPostAccessBlock? PostAccessBlock { get; init; }

		// Back-compat shim: the lead quest's STATE word (@20). World entry historically patched the live
		// character "level" here - the field is actually the first Quest_c record's state (for a fresh beach
		// char level == state == 1, so the write was a no-op). Kept so FullCharSnapshotSubClass/RetailLoginReplay
		// continue to compile and produce identical bytes.
		[JsonIgnore]
		public uint Level
		{
			get =>
				Quests.Records.Count > 0
					? Quests.Records[0].State
					: 0;
			set
			{
				if (Quests.Records.Count > 0)
					Quests.Records[0].State = value;
			}
		}

		// ICodec stream Read: the sub-class is the whole trailing region, so consume the rest and parse it.
		public static FullCharSnapshotSubClass Read(FaolanStream s)
		{
			return Deserialize(s.ReadRemainingArray());
		}

		public void Write(FaolanStream writer)
		{
			writer.WriteArray(Quests.ToArray());

			if (CompletedQuests != null)
				writer.WriteArray(CompletedQuests.ToArray());

			if (QuestIndex != null)
				writer.WriteArray(QuestIndex.ToArray());

			writer.WriteArray(UnknownBeforeAccessWindowTable);

			foreach (var entry in AccessWindowEntries)
				WriteAccessWindowEntry(writer, entry);

			if (PostAccessBlock != null)
				WritePostAccessBlock(writer, PostAccessBlock);
			else
				writer.WriteArray(UnknownAfterAccessWindowTable);
		}

		public byte[] ToArray()
		{
			using var writer = new FaolanStream();
			Write(writer);
			return writer.ToArray();
		}

		public static byte[] BuildEmptyQuestPortion()
		{
			using var w = new FaolanStream();
			w.WriteUInt32(QuestcRegion.Version); // 0x18
			w.WriteUInt32(QuestcRegion.Frame); // 0x3F1 - empty Quest_c list ((0+1)*0x3F1)
			w.WriteUInt32(ListFrame.Empty); // 0x3F1 - empty completed-journal frame (0 areas); NOT an "index tag" (#391)
			w.WriteUInt32(QuestcRegion.Frame); // 0x3F1 - empty index marker ((0+1)*0x3F1)
			w.WriteUInt32(0); // index count = 0
			w.WriteUInt32(EmptyQuestTerminal); // 0x000156E7
			return w.ToArray();
		}

		// Builds the WHOLE beach 0x737A6DF9 tails FROM SCRATCH - no captured-tails template (#186). Assembles the three
		// regions, each independently proven byte-exact: the generated quest portion (BuildQuestPortion, @0..1292),
		// the game-static access-window catalog (AccessWindowCatalog typed entries, @1292..7917), and the code stat
		// stat block (SubClassStatBlock, @7917..end) authored with the character's vitals/XP + learned spells. Then stamps
		// this session's playfield token. For the captured beach character + instance this reproduces
		// BeachTailsTemplate.bin byte-for-byte (BeachTailsFromScratchTests), so the template .bin is no longer needed.
		// Single-quest convenience (golden tests + the single-quest path): delegates to the multi-quest form with just
		// the beach quest. BuildFromScratch([beach], beach) == BuildFromScratch(beach), so byte-identical.
		public static byte[] BuildFromScratch(QuestDefinition beachDef, Character character, uint instance, uint? playfieldToken = null, IReadOnlyList<uint>? knownSpellIds = null,
			IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null)
		{
			ArgumentNullException.ThrowIfNull(beachDef);
			return BuildFromScratch([beachDef], beachDef, character, instance, playfieldToken, knownSpellIds, statSheet);
		}

		// The player's full set of snapshot-eligible held quests (#255): the journal region carries every held
		// quest's record + objective region (primary owns the index's terminal). For a beach-only char this equals
		// the beach-only overload. `primary` is the tracked quest (the beach quest when held). `knownSpellIds` are the
		// character's learned spells - built INTO the stat block's known-list so they persist across relog + place on
		// the action bar (#74/#71); null/empty leaves the list empty (byte-identical to the captured beach tails).
		public static byte[] BuildFromScratch(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, Character character, uint instance, uint? playfieldToken = null,
			IReadOnlyList<uint>? knownSpellIds = null, IReadOnlyList<(CharacterStatId Id, uint Value)>? statSheet = null,
			IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>? completedQuests = null, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(quests);
			// `primary` may be null IFF the character holds no snapshot quests (the empty-region create case); when
			// quests are present BuildQuestPortion enforces a non-null primary.

			var quest = BuildQuestPortion(quests, primary, instance, completedQuests, areas, areaCatalog);

			// #74/#71: the character's learned spells are built INTO the stat block's known-list (no-op when empty).
			var knownList = KnownSpellList.FromSpellIds(knownSpellIds);
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

			var imported = statSheet is { Count: > 0 };

			// Build the full stat SHEET and apply every BY-ID override to IT, THEN construct the block (with the known-
			// list) in one pass. The overrides MUST NOT be post-hoc SetStat calls on the built block: SetStat scans ALL
			// Values, which now include the inline known-list records, so a spell id colliding with an override stat id
			// would corrupt a record -> malformed known-list -> the client drops the connection. (The positional
			// SetValue ops below - CurrentXp/vitals/CurrentHp - stay in the stat region and never scan, so they are safe.)
			List<(uint Id, uint Value)> sheet;
			if (imported)
			{
				// The character's REAL captured sheet, with the character-derived XP-companion slots (0x15E MaxExp and
				// 0x39 ExpCurrentLevelBase - the current level's base XP, decoded #393) reset to baseline. XP (0x34) is
				// authored positionally below; the playfield-token slot (0xBF) is set for BOTH paths just below (from the
				// live zone token, not the stale captured one). NB this leaves the snapshot's XP bar internally
				// inconsistent for an imported char (base 0 / MaxExp 100 vs real Exp) - harmless only if the client reads
				// XP from the live StatIIR rather than this baseline sheet; preserving the captured MaxExp/base is a
				// candidate fix pending in-client check (the sheet is a baseline skeleton, see CharacterStatId).
				sheet = [.. statSheet!.Select(s => ((uint)s.Id, s.Value))];
				SetSheet(sheet, (uint)CharacterStatId.MaxExp, SubClassStatBlock.SkeletonValueOf((uint)CharacterStatId.MaxExp));
				SetSheet(sheet, (uint)CharacterStatId.ExpCurrentLevelBase, SubClassStatBlock.SkeletonValueOf((uint)CharacterStatId.ExpCurrentLevelBase));
			}
			else
			{
				// No captured sheet (a freshly-created character): the fresh skeleton with its LEVEL patched (the
				// skeleton hardcodes 1, so without this the client blocks every level-gated spell/item).
				sheet = SubClassStatBlock.FreshSheet();
				if (character != null)
					SetSheet(sheet, (uint)CharacterStatId.Level, character.Level);
			}

			// The playfield-instance token (stat slot 0xBF) is SESSION/MAP state, not a character stat: write the LIVE
			// zone token into it - so the client, the world objects, and the despawns all scope to the SAME token by
			// construction (no post-hoc patch). Falls back to the fresh/beach value when no live token is given (golden).
			SetSheet(sheet, SubClassStatBlock.PlayfieldTokenStatId, playfieldToken ?? SubClassStatBlock.SkeletonValueOf(SubClassStatBlock.PlayfieldTokenStatId));

			var block = SubClassStatBlock.CreateBlock(sheet, knownList);

			// Positional post-ops (SetValue-based, confined to the stat region - they do NOT scan the inline known-list).
			if (character != null)
			{
				block.CurrentXp = character.Experience;

				// A fresh character's per-class vitals (displayed + base/secondary slots) so the sheet is internally
				// consistent (#4/#186); an imported character already carries its real vitals in the sheet.
				if (!imported && character.MaxStats != null)
				{
					var v = character.MaxStats.SnapshotVitals(character.Class);
					block.ApplyClassVitals(v.Hp, v.HpBase, v.Mana, v.ManaBase, v.Stamina, v.StaminaBase);
				}

				// #214: a dead character logs in dead - zero current-health so the client shows the release window.
				if (character.IsDead && (imported || character.MaxStats != null))
					block.CurrentHp = 0;
			}

			var statBytes = SubClassStatBlock.Serialize(block);

			// The tails are FULLY CONSTRUCTED for the live session - no post-hoc byte-patch: the quest portion is built
			// with `instance`, the access-window + stat regions carry no per-session ids, and the playfield token is
			// authored into the stat block above (from the live zone). (Both the old ReplaceRawUInt32 template-instance
			// re-address and the WorldDespawn.TryReplacePlayfieldToken stamp are retired.)
			return [.. quest, .. AccessWindowTable, .. statBytes];
		}

		public static byte[] BuildQuestPortion(IReadOnlyList<QuestDefinition> quests, QuestDefinition primary, uint instance,
			IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)>? completed = null, IReadOnlyDictionary<uint, QuestArea>? areas = null,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>>? areaCatalog = null)
		{
			ArgumentNullException.ThrowIfNull(quests);

			// No ACTIVE snapshot quests -> the capture-proven empty region, and the completed journal is DROPPED
			// with it.
			//
			// ⚠️ That is deliberate. "Empty Quest_c list + completed journal" is a shape the corpus NEVER shows: all
			// 81 captured completed journals sit alongside a NON-EMPTY active journal (0 counter-examples in 104
			// captures). Emitting it anyway - which this did briefly - desynced the client and tripped
			// statlist.cpp:134 "inconsistent IdentityList", the same assert as #389.
			//
			// So the completed tab cannot ship before the ACTIVE journal carries quests: the two travel together.
			// Widening IsSnapshotEligible is the unblocking work (#389), not a change here.
			if (quests.Count == 0)
				return BuildEmptyQuestPortion();

			ArgumentNullException.ThrowIfNull(primary);

			// Wire order (decoded from the compeltefirstquest/Strie 2-quest region, byte-exact): the Quest_c list
			// header (version + count), then for EACH held quest its journal record IMMEDIATELY followed by its own
			// objective (@609) region, then the quest index. (The objective region's lead word is per-quest
			// (goals+1)*0x3F1.) The index is owned by the primary (tracked) quest - its goal-steps/table/trailer - but
			// lists every held quest's id. Primary ordered first.
			var ordered = quests.OrderByDescending(q => q.QuestId == primary.QuestId).ThenBy(q => q.QuestId).ToList();

			using var w = new FaolanStream();
			w.WriteUInt32(QuestcRegion.Version);
			w.WriteUInt32((ordered.Count + 1) * QuestcRegion.Frame);
			foreach (var q in ordered)
			{
				// The objective region is part of the RECORD (interleaved per quest), so it rides on the record and
				// QuestcRecord.Write emits it - keeping the writer symmetric with QuestcRegion.Read (#384).
				var record = Record(q);
				record.Objectives = QuestObjectiveRegion.Build(q, instance);
				record.Write(w);
			}

			// The COMPLETED-quest journal sits between the active journal and the index (#390) and is ALWAYS present -
			// as its own frame word even when empty (ListFrame.ForCount(0) == 0x3F1). That empty frame is exactly the
			// 0x3F1 the index USED to swallow as a phantom "IndexTag" (#391); emitting it here (and dropping the tag
			// from QuestIndexRegion) keeps the beach/2-quest goldens byte-identical while making the model correct.
			w.WriteArray(CompletedQuestJournalRegion.Build(completed ?? []).ToArray());

			// The quest index: all held questIds + one chained area-catalog block per quest, owned by the primary
			// (its trailer carries the terminal IndexTrailerWord). Modeled by the typed QuestIndexRegion codec.
			w.WriteArray(QuestIndexRegion.Build(ordered, primary, areas, areaCatalog).ToArray());

			return w.ToArray();

			static QuestcRecord Record(QuestDefinition q)
			{
				return QuestcRecord.BuildSingle(q, q.Level, q.SnapshotUnknown118, q.SnapshotUnknownEc, q.SnapshotUnknownF0, q.SnapshotUnknownF8, q.SnapshotUnknown28);
			}
		}

		// The single-quest quest portion (golden-test oracle for the multi-quest BuildQuestPortion: for one held quest
		// the two are byte-identical - see BuildQuestPortionTests).
		public static byte[] BuildSingleQuestPortion(QuestDefinition def, uint state, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);

			var record = QuestcRecord.BuildSingle(def, state, def.SnapshotUnknown118, def.SnapshotUnknownEc, def.SnapshotUnknownF0, def.SnapshotUnknownF8);
			record.Objectives = QuestObjectiveRegion.Build(def, instance);
			var quests = new QuestcRegion
			{
				Records =
				{
					record
				}
			};
			var index = QuestIndexRegion.Build([def], def);

			// The empty completed-journal frame (0x3F1) always sits between the active journal and the index (#391).
			byte[] portion = [.. quests.ToArray(), .. CompletedQuestJournalRegion.Build([]).ToArray(), .. index.ToArray()];
			if (portion.Length != QuestPortionLength)
				throw new InvalidOperationException($"generated quest portion is {portion.Length} B, expected {QuestPortionLength}");

			return portion;
		}

		public static FullCharSnapshotSubClass Deserialize(byte[] data)
		{
			ArgumentNullException.ThrowIfNull(data);

			// The tails open with the Quest_c journal region (version + 0x3F1-framed Quest_c list); parse it as a
			// typed region and take the boundary where it ends as the start of the remaining opaque blob.
			var quests = QuestcRegion.Parse(data, out var knownHeaderLength);

			// The @609 per-goal objective region is NOT a separate top-level region: it is interleaved per quest and
			// QuestcRegion already consumed each record's own (QuestcRecord.Objectives, #384). Parsing it again here
			// only worked because the beach journal holds exactly ONE quest, where "after the record" and "after the
			// last record" are the same offset. So the boundary past the journal is the journal's own end.
			var afterObjectives = knownHeaderLength;

			var accessWindowNameOffset = IndexOfAscii(data, "PFAccessWindow_");

			if (accessWindowNameOffset < 0)
			{
				var completedNoAw = TryParseCompletedJournal(data, afterObjectives, data.Length, out var afterCompletedNoAw);
				var questIndexNoAw = TryParseQuestIndex(data, afterCompletedNoAw, data.Length, out var afterIndexNoAw);
				var unknownBeforeAccessWindowTable = data[afterIndexNoAw..];

				return new FullCharSnapshotSubClass
				{
					Quests = quests,
					CompletedQuests = completedNoAw,
					QuestIndex = questIndexNoAw,
					KnownHeaderLength = knownHeaderLength,
					UnknownBeforeAccessWindowTable = unknownBeforeAccessWindowTable,
					AccessWindowEntries = [],
					UnknownAfterAccessWindowTable = [],
					PostAccessBlock = null,
					OriginalData = data
				};
			}

			// Top-level access entry prefix before the first PFAccessWindow_* string:
			//
			// u32 playfieldId
			// u8  sectionOrTier
			// u32 kind
			// u32 unknown
			// u16 internalNameLength
			var accessWindowTableStart = accessWindowNameOffset - 15;

			if (accessWindowTableStart < afterObjectives)
				accessWindowTableStart = afterObjectives;

			// The quest index sits between the @609 region and the access-window table; model it typed (its trailer
			// runs to the table start). On anything unexpected it stays in the opaque UnknownBefore blob.
			var completedQuests = TryParseCompletedJournal(data, afterObjectives, accessWindowTableStart, out var afterCompleted);
			var questIndex = TryParseQuestIndex(data, afterCompleted, accessWindowTableStart, out var afterIndex);
			var unknownBeforeAccessWindowTable0 = data[afterIndex..accessWindowTableStart];

			using var accessReader = new FaolanStream(data, false)
			{
				Position = checked((uint)accessWindowTableStart)
			};

			var accessWindowEntries = new List<PfAccessWindowEntry>();

			while (TryReadAccessWindowEntry(accessReader, data, out var accessWindowEntry))
				accessWindowEntries.Add(accessWindowEntry);

			var unknownAfterAccessWindowTable = data[checked((int)accessReader.Position)..];
			var postAccessBlock = TryReadPostAccessBlock(unknownAfterAccessWindowTable);

			return new FullCharSnapshotSubClass
			{
				Quests = quests,
				CompletedQuests = completedQuests,
				QuestIndex = questIndex,
				KnownHeaderLength = knownHeaderLength,
				UnknownBeforeAccessWindowTable = unknownBeforeAccessWindowTable0,
				AccessWindowEntries = accessWindowEntries,
				UnknownAfterAccessWindowTable = unknownAfterAccessWindowTable,
				PostAccessBlock = postAccessBlock,
				OriginalData = data
			};
		}

		// A quest can ride the snapshot journal region (BuildQuestPortion) only if its Quest_c scalars are seeded
		// (Level != 0) AND all 5 header text refs are present - else BuildSingle throws. Quests failing this
		// fall to the live RestorePlayerQuests path instead, so the two never deliver the same quest twice (#255).
		// Can this quest ride the login snapshot? The portion is Quest_c records + each quest's @609 objective region
		// + the quest INDEX, so a quest needs its record scalars, its five header text refs, its goals, AND its area.
		//
		// The index block's name word is the AREA's 4CC (29 of 29 captured blocks) and its id list is the area's quest
		// catalog - neither is per-quest. The gate here used to test QuestDefinition.IndexMagic, which models that
		// area-level word per quest and was therefore set on exactly two rows, so nothing passed.
		//
		// ⚠️ Getting this wrong is not a soft failure: an under-specified quest desyncs the client, which then dies in
		// StatList_t ("(nSize%1009)==0 && nSize>0, inconsistent IdentityList"). A round-trip through our own codec
		// will NOT catch that - reader and writer share the model - so widen this only with a byte-exact regeneration
		// test behind it.
		public static bool IsSnapshotEligible(QuestDefinition? def, QuestArea? area)
		{
			if (def is not { HasSnapshotRecord: true })
				return false;

			// Only quests whose objective region provably reproduces the wire (see
			// QuestDefinition.ObjectiveRegionVerified). The region has undecoded fields left, and an unreproducible
			// one takes the whole login down with it.
			if (def.Goals.Count == 0 || !def.ObjectiveRegionVerified || area is not { Magic: not 0 })
				return false;

			// ⚠️ TEMPORARILY OFF (#389). Everything above is satisfied by 9 of NecroL80's quests, but the @609 OBJECTIVE
			// region we would generate for them is not trustworthy yet: regenerating the corpus reproduces only 5 of
			// 99 quests byte-exact (CapturedObjectiveRegions_RebuildFromTheDatabase_ByteExact - 570 differ in length,
			// 1030 differ at equal length). Shipping it asserts the client on statlist.cpp:134.
			//
			// The quest index and the completed journal DO regenerate byte-exact (23/23 and 81/81), so this is the one
			// region left. Flip objectiveGateGreen to true when the @609 objective region reproduces the wire - not
			// before: a snapshot the client rejects takes the whole login with it, and the completed journal rides
			// along with the active one.
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse
			var objectiveGateGreen = false;
			if (!objectiveGateGreen)
				return false;

			var slots = def.TextRefs.Select(t => t.Slot).ToHashSet();
			return HeaderSlots.All(slots.Contains);
		}

		// For callers with no area to hand: never eligible, since the index cannot be built without one.
		public static bool IsSnapshotEligible(QuestDefinition? def)
		{
			return IsSnapshotEligible(def, null);
		}

		// Serializes a sequence of access-window entries to their wire bytes (the inverse of the parse loop in
		// Deserialize). Used by the from-scratch tails builder to emit the game-static PFAccessWindow_ catalog
		// (AccessWindowCatalog) instead of a captured byte blob; byte-identical to the captured region (#258).
		public static byte[] SerializeAccessWindowEntries(IEnumerable<PfAccessWindowEntry> entries)
		{
			using var writer = new FaolanStream();
			foreach (var entry in entries)
				WriteAccessWindowEntry(writer, entry);
			return writer.ToArray();
		}

		private static List<uint> CopyRange(List<uint> values, int startIndex, int length)
		{
			var result = new List<uint>(length);

			for (var i = 0; i < length; i++)
				result.Add(values[startIndex + i]);

			return result;
		}

		private static void ExpectByte(FaolanStream reader, byte expected)
		{
			var actualOffset = reader.Position;
			var actual = reader.ReadByte();

			if (actual != expected)
				throw new InvalidOperationException($"Unexpected byte at offset 0x{actualOffset:X8}. Expected 0x{expected:X2}, got 0x{actual:X2}.");
		}

		private static SubClassRepeatedUInt32Run? FindLargestAdjacentRepeatedRun(List<uint> values)
		{
			SubClassRepeatedUInt32Run? bestRun = null;

			for (var startIndex = 0; startIndex < values.Count; startIndex++)
			{
				var maxLength = (values.Count - startIndex) / 2;

				for (var length = maxLength; length >= 8; length--)
				{
					if (!UInt32RangesEqual(values, startIndex, startIndex + length, length))
						continue;

					if (bestRun == null || length > bestRun.Length)
					{
						bestRun = new SubClassRepeatedUInt32Run
						{
							StartIndex = startIndex,
							Length = length,
							RepeatCount = 2
						};
					}

					break;
				}
			}

			return bestRun;
		}

		private static int IndexOfAscii(byte[] data, string text, int startOffset = 0) // TODO: Convert to extension on byte[]
		{
			var pattern = Encoding.Latin1.GetBytes(text);

			for (var i = startOffset; i <= data.Length - pattern.Length; i++)
			{
				if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
					return i;
			}

			return -1;
		}

		private static bool LooksLikeAccessWindowEntryStart(byte[] data, int offset)
		{
			const int minimumPrefixLength = 4 + 1 + 4 + 4 + 2;

			if (offset < 0 || offset + minimumPrefixLength > data.Length)
				return false;

			var playfieldId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
			var sectionOrTier = data[offset + 4];
			var kind = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 5, 4));
			var nameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 13, 2));

			if (playfieldId is 0 or > 200_000)
				return false;

			if (sectionOrTier > 10)
				return false;

			if (kind > 10)
				return false;

			var nameOffset = offset + 15;
			var nameEndOffset = nameOffset + nameLength;

			if (nameEndOffset >= data.Length)
				return false;

			if (data[nameEndOffset] != 0)
				return false;

			return data.AsSpan(nameOffset, nameLength).StartsWith("PFAccessWindow_"u8);
		}

		private static PfAccessWindowChild ReadAccessWindowChild(FaolanStream reader, byte[] data, byte childMode)
		{
			var variant = reader.ReadByte();

			if (childMode == 1)
			{
				if (variant == 0)
				{
					var id = reader.ReadUInt32();
					var unknown = reader.ReadUInt32();

					return new PfAccessWindowChild
					{
						Variant = variant,
						Id = id,
						Unknown = unknown,
						DetailsMode = null,
						Details = []
					};
				}

				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = null,
					Unknown = null,
					DetailsMode = variant,
					Details = [ReadAccessWindowChildDetail(reader, variant)]
				};
			}

			if (childMode == 2)
			{
				var id = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();
				var detailsMode = reader.ReadByte();

				if (detailsMode == 0)
				{
					return new PfAccessWindowChild
					{
						Variant = variant,
						Id = id,
						Unknown = unknown,
						DetailsMode = detailsMode,
						Details = []
					};
				}

				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = id,
					Unknown = unknown,
					DetailsMode = detailsMode,
					Details = [ReadAccessWindowChildDetail(reader, detailsMode)]
				};
			}

			if (childMode == 3)
			{
				var id = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();

				var details = new List<PfAccessWindowChildDetail>();

				// Mode 3 has been observed on playfield 6300:
				// PFAccessWindow_ChosainProvince and PFAccessWindow_Shaulun.
				// No explicit detail count has been observed yet, so read detail
				// blocks until the next top-level access-window entry starts.
				while (!LooksLikeAccessWindowEntryStart(data, checked((int)reader.Position)))
				{
					if (Remaining(reader) <= 0)
						throw new InvalidOperationException("Unexpected end while reading childMode 3 details.");

					var detailsMode = reader.ReadByte();

					if (detailsMode == 0)
						throw new InvalidOperationException($"Unexpected childMode 3 detail mode 0 at offset 0x{reader.Position - 1:X8}.");

					details.Add(ReadAccessWindowChildDetail(reader, detailsMode));
				}

				return new PfAccessWindowChild
				{
					Variant = variant,
					Id = id,
					Unknown = unknown,
					DetailsMode = childMode,
					Details = details
				};
			}

			throw new InvalidOperationException($"Unsupported child mode {childMode} at offset 0x{reader.Position:X8}.");
		}

		private static PfAccessWindowChildDetail ReadAccessWindowChildDetail(FaolanStream reader, byte detailsMode)
		{
			var detailUnknown = reader.ReadByte();

			// These are usually readable strings like "M2W", but some values use bytes
			// above 0x7E, for example M2 0x84. Do not validate as strict ASCII here.
			var shortCode = ReadNullTerminatedLatin1String(reader);

			var displayName = ReadUInt8LengthPrefixedNullTerminatedLatin1String(reader);

			// No NUL here. In many records, the next 0 byte belongs to the next
			// playfield id, not to this string.
			var parentName = ReadUInt8LengthPrefixedLatin1String(reader);

			return new PfAccessWindowChildDetail
			{
				DetailsMode = detailsMode,
				DetailUnknown = detailUnknown,
				ShortCode = shortCode,
				DisplayName = displayName,
				ParentName = parentName
			};
		}

		private static string ReadNullTerminatedLatin1String(FaolanStream reader)
		{
			var bytes = new List<byte>();

			while (Remaining(reader) > 0)
			{
				var value = reader.ReadByte();

				if (value == 0)
					return Encoding.Latin1.GetString([.. bytes]);

				bytes.Add(value);
			}

			throw new InvalidOperationException($"Unexpected end of data while reading NUL-terminated string at offset 0x{reader.Position:X8}.");
		}

		private static string ReadUInt8LengthPrefixedLatin1String(FaolanStream reader)
		{
			var length = reader.ReadByte();
			var bytes = reader.ReadArray(length);

			return Encoding.Latin1.GetString(bytes);
		}

		private static string ReadUInt8LengthPrefixedNullTerminatedLatin1String(FaolanStream reader)
		{
			var text = ReadUInt8LengthPrefixedLatin1String(reader);
			ExpectByte(reader, 0);

			return text;
		}

		private static uint Remaining(FaolanStream stream)
		{
			return stream.Length - stream.Position;
		}

		// Sets a stat id's value at EVERY occurrence in the sheet (a few ids appear in both StatList#1 and StatList#2).
		// Applied to the sheet BEFORE the block is built, so - unlike SubClassPostAccessBlock.SetStat - it never scans the
		// inline known-list records.
		private static void SetSheet(List<(uint Id, uint Value)> sheet, uint statId, uint value)
		{
			for (var i = 0; i < sheet.Count; i++)
			{
				if (sheet[i].Id == statId)
					sheet[i] = (statId, value);
			}
		}

		private static SubClassPostAccessLayout? TryBuildPostAccessLayout(List<uint> values)
		{
			var repeatedRun = FindLargestAdjacentRepeatedRun(values);

			if (repeatedRun == null)
				return null;

			var prefix = CopyRange(values, 0, repeatedRun.StartIndex);
			var repeatedValues = CopyRange(values, repeatedRun.StartIndex, repeatedRun.Length);
			var footerStartIndex = repeatedRun.StartIndex + repeatedRun.Length * repeatedRun.RepeatCount;
			var footer = CopyRange(values, footerStartIndex, values.Count - footerStartIndex);

			return new SubClassPostAccessLayout
			{
				PrefixValues = prefix,
				RepeatedValues = repeatedValues,
				RepeatCount = repeatedRun.RepeatCount,
				FooterValues = footer
			};
		}

		// Parses the COMPLETED-quest journal, which ALWAYS precedes the quest index (#390/#391). Its frame word is
		// (areas + 1) * 0x3F1, so a character with no completed quests still carries the empty frame 0x3F1 - the word
		// that used to be mis-read as the index's "IndexTag". Returns null - leaving the bytes opaque - only when the
		// boundary word is not a ListFrame word at all.
		private static CompletedQuestJournalRegion? TryParseCompletedJournal(byte[] data, int offset, int endOffset, out int afterRegion)
		{
			afterRegion = offset;
			if (offset + 4 > data.Length || endOffset < offset)
				return null;

			var lead = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
			if (lead == 0 || lead % ListFrame.Unit != 0)
				return null;

			// The lead word already says a region STARTS here (an area-count frame, 0x3F1 when empty), so a parse
			// failure means our model is wrong - not that the region is absent. Swallowing it drops the bytes into
			// the opaque blob, which is exactly how the completed-quest journal stayed unmodelled while every snapshot
			// still round-tripped (#390).
			var region = CompletedQuestJournalRegion.Parse(data, offset, endOffset, out var after);
			afterRegion = after;
			return region;
		}

		// Parses the quest index, which follows the completed-journal frame and opens with its id-list marker
		// (count + 1) * 0x3F1 - NOT a 0x3F1 "IndexTag" (that phantom tag was the empty completed-journal frame, #391).
		// Its trailer runs to `endOffset` (the access-window table start). Returns null (and leaves `afterIndex ==
		// offset`) when the boundary is not a marker word, so an unrecognised shape stays in the opaque blob.
		private static QuestIndexRegion? TryParseQuestIndex(byte[] data, int offset, int endOffset, out int afterIndex)
		{
			afterIndex = offset;
			if (offset + 4 > data.Length || endOffset < offset)
				return null;

			var marker = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
			if (marker == 0 || marker % ListFrame.Unit != 0)
				return null;

			// The marker says an index starts here, so failing to parse it is a MODEL error, not an absent region.
			var region = QuestIndexRegion.Parse(data, offset, endOffset);
			afterIndex = endOffset; // the region consumes to the boundary (its trailer is the remainder)
			return region;
		}

		private static bool TryReadAccessWindowEntry(FaolanStream reader, byte[] data, out PfAccessWindowEntry entry)
		{
			entry = null!;

			var startOffset = checked((int)reader.Position);

			if (!LooksLikeAccessWindowEntryStart(data, startOffset))
				return false;

			try
			{
				var playfieldId = reader.ReadUInt32();
				var sectionOrTier = reader.ReadByte();
				var kind = reader.ReadUInt32();
				var unknown = reader.ReadUInt32();
				var internalName = reader.ReadString();

				ExpectByte(reader, 0);

				if (!internalName.StartsWith("PFAccessWindow_", StringComparison.Ordinal))
				{
					reader.Position = checked((uint)startOffset);
					return false;
				}

				var unknownBeforeChildMode = reader.ReadUInt16();
				var childMode = reader.ReadByte();

				if (childMode > 3)
				{
					reader.Position = checked((uint)startOffset);
					return false;
				}

				PfAccessWindowChild? child = null;

				if (childMode != 0)
					child = ReadAccessWindowChild(reader, data, childMode);

				entry = new PfAccessWindowEntry
				{
					Offset = startOffset,
					PlayfieldId = playfieldId,
					SectionOrTier = sectionOrTier,
					Kind = kind,
					Unknown = unknown,
					InternalName = internalName,
					UnknownBeforeChildMode = unknownBeforeChildMode,
					ChildMode = childMode,
					Child = child
				};

				return true;
			}
			catch
			{
				reader.Position = checked((uint)startOffset);
				return false;
			}
		}

		private static SubClassPostAccessBlock? TryReadPostAccessBlock(byte[] data)
		{
			if (data.Length < 12)
				return null;

			var marker0 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
			var marker1 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
			var blockType = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));

			// Observed post-access block starts with:
			// 0x000003F1, 0x000003F1, 0x000046F2
			if (marker0 != ListFrame.Empty || marker1 != ListFrame.Empty) // 0x3F1 = an EMPTY count-framed list
				return null;

			var payloadLength = data.Length - 12;
			var trailerLength = -1;

			// Treat this as a uint stream first, not as key/value pairs. The previous
			// pair view was useful diagnostically, but the output shows repeated numeric
			// ranges that do not behave like a flat dictionary.
			for (var candidateTrailerLength = 0; candidateTrailerLength <= 16; candidateTrailerLength++)
			{
				if (payloadLength >= candidateTrailerLength && (payloadLength - candidateTrailerLength) % 4 == 0)
				{
					trailerLength = candidateTrailerLength;
					break;
				}
			}

			if (trailerLength < 0)
				return null;

			var valueBytesLength = payloadLength - trailerLength;
			var valueCount = valueBytesLength / 4;
			var values = new List<uint>(valueCount);

			var offset = 12;

			for (var index = 0; index < valueCount; index++)
			{
				values.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
				offset += 4;
			}

			var trailer = data[offset..];

			return new SubClassPostAccessBlock
			{
				Marker0 = marker0,
				Marker1 = marker1,
				BlockType = blockType,
				Values = values,
				LargestAdjacentRepeatedRun = FindLargestAdjacentRepeatedRun(values),
				Layout = TryBuildPostAccessLayout(values),
				Trailer = trailer
			};
		}

		private static bool UInt32RangesEqual(List<uint> values, int leftStart, int rightStart, int length)
		{
			for (var i = 0; i < length; i++)
			{
				if (values[leftStart + i] != values[rightStart + i])
					return false;
			}

			return true;
		}

		private static void WriteAccessWindowChild(FaolanStream writer, byte childMode, PfAccessWindowChild child)
		{
			writer.WriteByte(child.Variant);

			if (childMode == 1)
			{
				if (child.Variant == 0)
				{
					writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 1 variant 0 requires Id."));
					writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 1 variant 0 requires Unknown."));
					return;
				}

				WriteSingleAccessWindowChildDetail(writer, child, child.Variant);
				return;
			}

			if (childMode == 2)
			{
				writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 2 requires Id."));
				writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 2 requires Unknown."));

				var detailsMode = child.DetailsMode ?? 0;
				writer.WriteByte(detailsMode);

				if (detailsMode != 0)
					WriteSingleAccessWindowChildDetail(writer, child, detailsMode);

				return;
			}

			if (childMode == 3)
			{
				writer.WriteUInt32(child.Id ?? throw new InvalidOperationException("childMode 3 requires Id."));
				writer.WriteUInt32(child.Unknown ?? throw new InvalidOperationException("childMode 3 requires Unknown."));

				foreach (var detail in child.Details)
				{
					writer.WriteByte(detail.DetailsMode);
					WriteAccessWindowChildDetailBody(writer, detail);
				}

				return;
			}

			throw new InvalidOperationException($"Unsupported child mode {childMode}.");
		}

		private static void WriteAccessWindowChildDetailBody(FaolanStream writer, PfAccessWindowChildDetail detail)
		{
			writer.WriteByte(detail.DetailUnknown);
			WriteNullTerminatedLatin1String(writer, detail.ShortCode);
			WriteUInt8LengthPrefixedNullTerminatedLatin1String(writer, detail.DisplayName);
			WriteUInt8LengthPrefixedLatin1String(writer, detail.ParentName);
		}

		private static void WriteAccessWindowEntry(FaolanStream writer, PfAccessWindowEntry entry)
		{
			writer.WriteUInt32(entry.PlayfieldId);
			writer.WriteByte(entry.SectionOrTier);
			writer.WriteUInt32(entry.Kind);
			writer.WriteUInt32(entry.Unknown);
			writer.WriteString(entry.InternalName);
			writer.WriteByte(0);
			writer.WriteUInt16(entry.UnknownBeforeChildMode);
			writer.WriteByte(entry.ChildMode);

			if (entry.ChildMode != 0)
			{
				if (entry.Child == null)
					throw new InvalidOperationException($"Access-window entry at 0x{entry.Offset:X8} has childMode {entry.ChildMode}, but Child == null.");

				WriteAccessWindowChild(writer, entry.ChildMode, entry.Child);
			}
		}

		private static void WriteNullTerminatedLatin1String(FaolanStream writer, string value)
		{
			writer.WriteArray(Encoding.Latin1.GetBytes(value));
			writer.WriteByte(0);
		}

		private static void WritePostAccessBlock(FaolanStream writer, SubClassPostAccessBlock block)
		{
			writer.WriteUInt32(block.Marker0);
			writer.WriteUInt32(block.Marker1);
			writer.WriteUInt32(block.BlockType);

			foreach (var value in block.Values)
				writer.WriteUInt32(value);

			writer.WriteArray(block.Trailer);
		}

		private static void WriteSingleAccessWindowChildDetail(FaolanStream writer, PfAccessWindowChild child, byte expectedDetailsMode)
		{
			if (child.Details.Count != 1)
				throw new InvalidOperationException($"Expected exactly one detail for detailsMode {expectedDetailsMode}, got {child.Details.Count}.");

			var detail = child.Details[0];

			if (detail.DetailsMode != expectedDetailsMode)
				throw new InvalidOperationException($"Detail mode mismatch. Expected {expectedDetailsMode}, got {detail.DetailsMode}.");

			WriteAccessWindowChildDetailBody(writer, detail);
		}

		private static void WriteUInt8LengthPrefixedLatin1String(FaolanStream writer, string value)
		{
			var bytes = Encoding.Latin1.GetBytes(value);
			writer.WriteByte(checked((byte)bytes.Length));
			writer.WriteArray(bytes);
		}

		private static void WriteUInt8LengthPrefixedNullTerminatedLatin1String(FaolanStream writer, string value)
		{
			WriteUInt8LengthPrefixedLatin1String(writer, value);
			writer.WriteByte(0);
		}
	}

	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }
		public byte ChildMode { get; init; }

		public byte SectionOrTier { get; init; }
		public int Offset { get; init; }

		public PfAccessWindowChild? Child { get; init; }

		public string InternalName { get; init; } = null!;

		public uint Kind { get; init; }

		public uint Unknown { get; init; }

		public ushort UnknownBeforeChildMode { get; init; }

		public override string ToString()
		{
			var child = Child == null
				? ""
				: $" | child: {Child}";

			return $"0x{Offset:X8}: {PlayfieldId} {InternalName} section={SectionOrTier} kind={Kind} childMode={ChildMode}{child}";
		}
	}

	public sealed class PfAccessWindowChild
	{
		public bool HasDetails => Details.Count != 0;
		public byte Variant { get; init; }

		public byte? DetailsMode { get; init; }

		public List<PfAccessWindowChildDetail> Details { get; init; } = [];

		public uint? Id { get; init; }

		public uint? Unknown { get; init; }

		public override string ToString()
		{
			if (!HasDetails)
			{
				return Id == null
					? $"variant={Variant}"
					: $"variant={Variant}, id=0x{Id:X8} ({Id})";
			}

			var id = Id == null
				? ""
				: $"id=0x{Id:X8} ({Id}), ";

			if (Details.Count == 1)
				return $"variant={Variant}, {id}{Details[0]}";

			return $"variant={Variant}, {id}details={Details.Count}";
		}
	}

	public sealed class PfAccessWindowChildDetail
	{
		public byte DetailsMode { get; init; }

		public byte DetailUnknown { get; init; }

		public string DisplayName { get; init; } = null!;

		public string ParentName { get; init; } = null!;

		public string ShortCode { get; init; } = null!;

		public override string ToString()
		{
			return $"mode={DetailsMode}, code={ShortCode}, display={DisplayName}, parent={ParentName}";
		}
	}

	public sealed class SubClassPostAccessBlock
	{
		private const int RepeatedBlockEndIndex = 182;
		private const int RepeatedBlockOffset = 148;
		private const int RepeatedBlockStartIndex = 35;

		public byte[] Trailer { get; init; } = [];

		public List<uint> Values { get; init; } = [];

		public SubClassPostAccessLayout? Layout { get; init; }

		public SubClassRepeatedUInt32Run? LargestAdjacentRepeatedRun { get; init; }

		public uint BlockType { get; init; }

		public uint CurrentHp
		{
			get => GetValue(CharacterValueIndex.CurrentHp) / 100;
			set
			{
				var scaledValue = value * 100;

				Values[(int)CharacterValueIndex.CurrentHpExtra] = scaledValue;
				SetValue(CharacterValueIndex.CurrentHp, scaledValue);
			}
		}

		public uint CurrentMana
		{
			get => GetValue(CharacterValueIndex.CurrentMana) / 100;
			set => SetValue(CharacterValueIndex.CurrentMana, value * 100);
		}

		public uint CurrentStamina
		{
			get => GetValue(CharacterValueIndex.CurrentStamina) / 100;
			set => SetValue(CharacterValueIndex.CurrentStamina, value * 100);
		}

		public uint CurrentXp
		{
			get => GetValue(CharacterValueIndex.CurrentXp) / 10;
			set => SetValue(CharacterValueIndex.CurrentXp, value * 10);
		}

		public uint Marker0 { get; init; }

		public uint Marker1 { get; init; }

		//public uint PvpXpTillNextLevel
		//{
		//	get => GetValue(CharacterValueIndex.PvpXpTillNextLevel);
		//	set => SetValue(CharacterValueIndex.PvpXpTillNextLevel, value);
		//}

		/*public uint PvpKillsOverall
		{
			get => GetValue(CharacterValueIndex.PvpKillsOverall);
			set => SetValue(CharacterValueIndex.PvpKillsOverall, value);
		}*/

		public uint PvpDeathsOverall
		{
			get => GetValue(CharacterValueIndex.PvpDeathsOverall);
			set => SetValue(CharacterValueIndex.PvpDeathsOverall, value);
		}

		public enum CharacterValueIndex : uint
		{
			// HP is special:
			// - Extra pre-block slot: 23
			// - Main repeated-block slot: 54
			// - Copy slot: 202
			CurrentHpExtra = 23,

			Unknown35 = 35,
			Unknown36 = 36,
			Unknown37 = 37,
			Unknown38 = 38,
			Unknown39 = 39,
			Unknown40 = 40,
			Unknown41 = 41,
			Unknown42 = 42,
			Unknown43 = 43,
			Unknown44 = 44, // PvpXpTillNextLevel?
			Unknown45 = 45,
			PvpXp = 46,
			Unknown47 = 47,
			Unknown48 = 48,
			Unknown49 = 49,
			Unknown50 = 50,
			Unknown51 = 51,
			Unknown52 = 52,
			Unknown53 = 53,
			CurrentHp = 54,
			Unknown55 = 55,
			Unknown56 = 56,
			Unknown57 = 57,
			Unknown58 = 58,
			Unknown59 = 59,
			Unknown60 = 60,
			Unknown61 = 61,

			// Index 62 is a SECONDARY mana value (~6 lower than displayed): retail Necro [62]=24000 vs displayed
			// 24600, Bear Shaman [62]=16500 vs displayed 17100. NOT the displayed max — see CurrentMana below.
			ManaSecondary = 62,
			Unknown63 = 63,

			// The displayed MAX mana (×100). Verified against retail LOGIN snapshots: Necro [64]=24600 (=246 mana,
			// matches the client sheet), Bear Shaman [64]=17100 (=171). Index 60 carries the same value; [62] is a
			// ~6-lower secondary. (The earlier "[64]=2276 / use 62" note came from an UNSETTLED create snapshot.)
			CurrentMana = 64,
			Unknown65 = 65,
			Unknown66 = 66,
			Unknown67 = 67,
			CurrentStamina = 68,
			Unknown69 = 69,
			Unknown70 = 70,
			Unknown71 = 71,
			Unknown72 = 72,
			Unknown73 = 73,
			Unknown74 = 74,
			Unknown75 = 75,
			Unknown76 = 76,
			Unknown77 = 77,
			Unknown78 = 78,
			Unknown79 = 79,
			CurrentXp = 80, // factor 10
			Unknown81 = 81,
			Unknown82 = 82,
			Unknown83 = 83,
			Unknown84 = 84,
			Unknown85 = 85,
			PvpLevel = 86,
			Unknown87 = 87,
			Unknown88 = 88,
			Unknown89 = 89,
			Unknown90 = 90,
			Unknown91 = 91,
			Unknown92 = 92,
			Unknown93 = 93,
			Unknown94 = 94,
			Unknown95 = 95,
			Unknown96 = 96,
			Unknown97 = 97,
			Unknown98 = 98,
			Unknown99 = 99,
			Unknown100 = 100,
			Unknown101 = 101,
			Unknown102 = 102,
			Unknown103 = 103,
			Unknown104 = 104,
			Unknown105 = 105,
			Unknown106 = 106,
			Unknown107 = 107,
			Unknown108 = 108,
			Unknown109 = 109,
			Unknown110 = 110,
			Unknown111 = 111,
			Unknown112 = 112,
			Unknown113 = 113,
			Unknown114 = 114,
			Unknown115 = 115,
			Unknown116 = 116,
			Unknown117 = 117,
			Unknown118 = 118,
			Unknown119 = 119,
			Unknown120 = 120,
			Unknown121 = 121,
			Unknown122 = 122,
			Unknown123 = 123,
			Unknown124 = 124,
			Unknown125 = 125,
			Unknown126 = 126,
			Unknown127 = 127,
			Unknown128 = 128,
			Unknown129 = 129,
			Unknown130 = 130,
			Unknown131 = 131,
			Unknown132 = 132,
			Unknown133 = 133,
			Unknown134 = 134,
			Unknown135 = 135,
			Unknown136 = 136,
			Unknown137 = 137,
			Unknown138 = 138,
			Unknown139 = 139,
			Unknown140 = 140,
			Unknown141 = 141,
			Unknown142 = 142,
			Unknown143 = 143,
			PvpMinigamesAbandonedSeason = 144,
			Unknown145 = 145,
			PvpMinigamesAbandonedOverall = 146,
			Unknown147 = 147,
			PvpSiegeLosesSeason = 148,
			Unknown149 = 149,
			PvpSiegeLosesOverall = 150,
			Unknown151 = 151,
			PvpSiegeWinsSeason = 152,
			Unknown153 = 153,
			PvpSiegeWinsOverall = 154,
			Unknown155 = 155,
			PvpCaptureTheSkullLossesSeason = 156,
			Unknown157 = 157,
			PvpCaptureTheSkullLossesOverall = 158,
			Unknown159 = 159,
			PvpCaptureTheSkullWinsSeason = 160,
			Unknown161 = 161,
			PvpCaptureTheSkullWinsOverall = 162,
			Unknown163 = 163,
			PvpAnnihilationLossesSeason = 164,
			Unknown165 = 165,
			PvpAnnihilationLossesOverall = 166,
			Unknown167 = 167,
			PvpAnnihilationWinsSeason = 168,
			Unknown169 = 169,
			PvpAnnihilationWinsOverall = 170,
			Unknown171 = 171,
			PvpFatalitiesSeason = 172,
			Unknown173 = 173,
			PvpFatalitiesOverall = 174,
			Unknown175 = 175,
			PvpDeathSeason = 176,
			Unknown177 = 177,
			PvpDeathsOverall = 178,
			Unknown179 = 179,
			PvpKillsSeason = 180, // factor 10
			Unknown181 = 181,
			Unknown182 = 182 // PvpKillsOverall ?
		}

		// Authors the WHOLE per-class vitals block (#4/#186): for HP/Mana/Stamina it sets the DISPLAYED max AND the
		// lower base/secondary the client reads into a parallel slot, each ×100, keeping the +148 repeated copies in
		// sync. The old per-stat setters (CurrentHp/Mana/Stamina) touched only the displayed slot, leaving the base
		// slots - and the second mana copy [60] - at the captured Strie (Rogue) template's values; that internal
		// inconsistency is the suspected cause of #4 (a Necromancer showed ~0 mana because [60] stayed 0 while [64]
		// held 24600) and the in-client "gear vanished" snapshot reject. Verified slot map across four archetypes'
		// retail LOGIN snapshots (decode_statblock.py): HP displayed [23]/[54], base [21]/[56]; mana displayed
		// [60]=[64], secondary [62]; stamina displayed [68], secondary [66]. Pass CharacterMaxStats.SnapshotVitals().
		public void ApplyClassVitals(uint hp, uint hpBase, uint mana, uint manaBase, uint stamina, uint staminaBase)
		{
			// HP: [21]/[23] are pre-repeated-block standalone slots (like CurrentHpExtra, no +148 copy); [54]/[56] live
			// inside the repeated block, so SetValue also writes their [202]/[204] copies.
			Values[21] = hpBase * 100;
			Values[(int)CharacterValueIndex.CurrentHpExtra] = hp * 100; // [23]
			SetValue(CharacterValueIndex.CurrentHp, hp * 100); // [54] (+[202])
			SetValue((CharacterValueIndex)56, hpBase * 100); // [56] (+[204])

			// Mana: the displayed max lives in BOTH [60] and [64]; [62] is the ~6-lower secondary.
			SetValue((CharacterValueIndex)60, mana * 100); // [60] (+[208])
			SetValue(CharacterValueIndex.ManaSecondary, manaBase * 100); // [62] (+[210])
			SetValue(CharacterValueIndex.CurrentMana, mana * 100); // [64] (+[212])

			// Stamina: displayed [68], secondary [66].
			SetValue((CharacterValueIndex)66, staminaBase * 100); // [66] (+[214])
			SetValue(CharacterValueIndex.CurrentStamina, stamina * 100); // [68] (+[216])
		}

		public uint GetValue(CharacterValueIndex index)
		{
			return Values[(int)index];
		}

		// The block is a [statId, value] stat sheet: a stat's value is the slot immediately after each occurrence of
		// its id (a stat appears in the leading displayed section, the repeated run, AND the run's +148 copy). Sets the
		// value at EVERY occurrence so the sheet is internally consistent. Used to DERIVE character stats (Level 0x36,
		// XP 0x34, MaxXP 0x15E, ...) from the character instead of carrying the skeleton's fixed value - the fix for
		// hardcoded Level: the 313-value "fixed skeleton" was diffed only across level-1 characters, so the level slot
		// never varied and was misclassified as fixed (it is really character-derived).
		public void SetStat(uint statId, uint value)
		{
			for (var i = 0; i + 1 < Values.Count; i++)
			{
				if (Values[i] == statId)
					Values[i + 1] = value;
			}
		}

		public void SetValue(CharacterValueIndex index, uint value)
		{
			Values[(int)index] = value;
			Values[GetRepeatedCopyIndex(index)] = value;
		}

		private static int GetRepeatedCopyIndex(CharacterValueIndex index)
		{
			var indexx = (int)index;

			if (indexx is < RepeatedBlockStartIndex or > RepeatedBlockEndIndex)
				throw new ArgumentOutOfRangeException(nameof(indexx), indexx, $"Index must be inside repeated block {RepeatedBlockStartIndex}..{RepeatedBlockEndIndex}.");

			return indexx + RepeatedBlockOffset;
		}
	}

	public sealed class SubClassPostAccessLayout
	{
		public int RepeatCount { get; init; }

		public List<uint> FooterValues { get; init; } = [];
		public List<uint> PrefixValues { get; init; } = [];

		/// <summary>
		///     Repeated post-access uint payload.
		///     Strong observations from screenshots + parsed post-access data:
		///     - RepeatedValues[18] = 27, RepeatedValues[19] = current health scaled by 100.
		///     bigboy-1: 594900 -> 5949 health.
		///     bigboy-2: 9800 -> 98 health.
		///     bigboy-3 screenshot expects 8900 -> 89 health.
		///     bigboy-4 screenshot expects 594900 -> 5949 health.
		///     - RepeatedValues[24] = 506, RepeatedValues[25] = current mana scaled by 100.
		///     bigboy-1: 583000 -> 5830 mana.
		///     bigboy-2: 0 -> 0 mana.
		///     bigboy-3 screenshot expects 24600 -> 246 mana.
		///     bigboy-4 screenshot expects 583000 -> 5830 mana.
		///     - RepeatedValues[28] = 506, RepeatedValues[29] = another mana-like copy in some files,
		///     but not proven to be max mana. bigboy-2 and bigboy-3 show max mana separately in UI.
		///     bigboy-1: 583000 -> same as current mana.
		///     bigboy-2: 0 -> same as current mana.
		///     bigboy-3 max mana screenshot value is 346000, so search for that separately.
		///     bigboy-4: 583000 -> same as current mana.
		///     - RepeatedValues[30] = 505, RepeatedValues[31] = stamina-like value scaled by 100.
		///     bigboy-2 screenshot: 23000 -> 230, but UI shows 236 / 230, so this is likely max stamina.
		///     - RepeatedValues[32] = 504, RepeatedValues[33] = stamina-like value scaled by 100.
		///     bigboy-2 screenshot: 23600 -> 236, so this is likely current stamina.
		///     - RepeatedValues[48] = 54, RepeatedValues[49] = level.
		///     bigboy-1: 80.
		///     bigboy-2: 1.
		///     bigboy-3 screenshot expects 1.
		///     bigboy-4 screenshot expects 80.
		///     Keep these as comments for now; this block is still preserved as raw uints
		///     so write-back stays lossless while the exact stat table format is decoded.
		/// </summary>
		public List<uint> RepeatedValues { get; init; } = [];
	}

	public sealed class SubClassRepeatedUInt32Run
	{
		public int Length { get; init; }

		public int RepeatCount { get; init; }

		public int SecondStartIndex => StartIndex + Length;
		public int StartIndex { get; init; }
	}
}

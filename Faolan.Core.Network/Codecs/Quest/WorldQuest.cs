using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	// Pure quest objective-tracking rules (docs/archive/quests-and-combat.md) - testable without the DB/network.
	public static class QuestProgress
	{
		// True if a trigger of `triggerType` against `targetName` advances this quest's goal (#261): the quest is Active,
		// its first goal is of that type and names that target, and the objective isn't already complete. The trigger
		// fires per goal type - a kill (CombatService), a corpse-bag loot (LootService), or a talk (ConversationService).
		// Required is clamped to >= 1 so a goal seeded with RequiredCount 0 still completes on the first trigger.
		public static bool Advances(CharacterQuest quest, QuestGoalDefinition goal, QuestGoalType triggerType, string targetName)
		{
			return quest.State == QuestState.Active && goal != null && goal.GoalType == triggerType && !string.IsNullOrEmpty(goal.TargetName) && !string.IsNullOrEmpty(targetName) &&
				   goal.TargetName.Equals(targetName, StringComparison.OrdinalIgnoreCase) && quest.GoalProgress < Math.Max(quest.GoalRequired, 1);
		}

		// True if killing an NPC named `npcName` advances this quest's kill objective (active, matching, not yet done).
		public static bool AdvancesOnKill(CharacterQuest quest, string npcName)
		{
			return quest.State == QuestState.Active && !string.IsNullOrEmpty(quest.GoalTargetName) && !string.IsNullOrEmpty(npcName) && quest.GoalTargetName.Equals(npcName, StringComparison.OrdinalIgnoreCase) &&
				   quest.GoalProgress < quest.GoalRequired;
		}
	}

	// Quest state packets, decoded from captures (docs/archive/quests-and-combat.md). These build the 0x2000 IIR record
	// bodies (after the u32 length prefix). Quest text (title/desc/journal) is NOT sent here - the client renders it
	// from its RDB by quest id; the server only drives quest STATE + objective progress. The full accept/branch logic
	// is the pinned RDB Lua quest VM.
	public static class WorldQuest
	{
		// BuildQuestFullUpdate emits [subop 4][ack 4][player 4][u32 0 = 4][0x01 = 1] = 17 bytes before `questId`; the
		// per-quest snapshot-tails content is that output from `questId` onward (used by the snapshot-shape tests).
		public const int QuestFullUpdateContentOffset = 17;
		public const uint AreaTitleCategory = 50300; // category of the area journal-title remoteformat

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 ("Shipwrecked") in its tails, with its
		// FULL multi-objective journal record, for every character (snapshot_embeds_strie_quest). The snapshot is the
		// authoritative delivery for it - the server must NOT re-send a (thinner, single-objective) QuestFullUpdate for
		// it on login, or it clobbers the snapshot's richer entry. Persisted via EnsureBeachStarterQuest; skipped in
		// RestorePlayerQuests.
		public const uint BeachStarterQuestId = 10238;

		// #262 the Casilda intro-chain ESCORT stage (the final stage: 13479 kill -> 13563 loot -> 13480 talk -> 12407
		// escort). While this stage is active the player has "freed" Casilda and she follows them as their escort; on
		// login at this stage the follow is restored (BurstlessWorldEntry). Completing it ends the escort (she stops).
		public const uint CasildaEscortQuestId = 12407;

		// The QuestFullUpdate (0x63268258) carries its OWN per-quest "state/category" field (the u32 after the second
		// questId) - a DIFFERENT enum from the QuestIIR StateActive above. It is CONSTANT per quest across its whole
		// lifecycle (every captured 13479 full-update uses 3, on grant and on every re-display: frames 687/8876/9947/
		// 10129), so it is a quest property, not a dynamic state. Passing the QuestIIR value (2) here makes the client
		// silently DROP the full-update (journal stays empty) - the bug behind the dialogue-grant not rendering.
		// TODO: harvest this per quest into QuestDefinition; 3 is the captured value for Casilda's 13479 (and the only
		// value observed for active quests so far - a different quest 0x..27 used 1).
		public const uint FullUpdateStateActive = 3;

		// --- Journal CONTENT packets (Captures/scripts/decode_quest.py, byte-exact golden tests) ----------
		//
		// These carry the J-panel text the player reads. Text is NEVER literal: every slot is a
		// <remoteformat .../> or <localized .../> token packed as  00 <u8 len> <ascii>.  The client
		// resolves (id, category) against its own RDB; the `key` attribute is a DETERMINISTIC content
		// hash (identical across characters/sessions/captures), so it is harvested data, not a nonce.
		// Builders emit from the subopcode onward; the 4-byte length prefix is added by the send layer
		// (matching BuildQuestIir/BuildQuestGoal).


		// Quest state codes (from the capture). On completion retail sends QuestIIR twice for the quest: state 2
		// (active/update) then state 4 (completed) - see the Stihetfv compeltefirstquest capture (13479 idx858/860).
		public const uint StateActive = 2;
		public const uint StateCompleted = 4;

		// The fixed u64 the QuestFullUpdate carries at its `acceptTime` slot inside the snapshot tails (NOT a live
		// timestamp - it is the same constant in the live grant packet and the in-progress login snapshot; decoded as
		// a structural header field, docs/quest-log-handover.md).
		public const ulong SnapshotQuestAcceptTime = 0x000111D354375554UL;
		private const string RrlMagic = "RRL3"; // record-list magic that precedes the per-quest entries
		private const uint ChapterTag = 0x00000BD3; // per-quest grouping word, constant across Tortage quests
		private const uint ListFrame = 0x000003F1; // the (count+1)*0x3F1 list-framing invariant; 0x3F1 = an empty list

		// QuestFullUpdate: one quest's full record (description, summary, objective + world marker).
		//   header: [00000000][01][questId][00000001][questId][state][00000000][00000003][0x412]
		//   <title 50301> <summary 50302> <areaTitle 50300> <region 52000> <map 52000>
		//   <stat/objective run carrying areaId, questId, goalId, player, acceptTime, map+x+y marker>
		//   <objective text 50304> <objective localized> [00]
		// The mid-run scalars (0x50/0x64/0x7e2/0x0c ...) are not yet semantically pinned; they are
		// reproduced as a documented template and locked by the golden test.
		public static byte[] BuildQuestFullUpdate(uint playerHandle, uint questId, uint state, uint areaId, IReadOnlyList<byte[]> textRefs, uint goalId, ulong acceptTime, uint mapId, uint markerX, uint markerY,
			byte[] objectiveText, byte[] objectiveLocalized)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestFullUpdateIIR, playerHandle);
			w.WriteUInt32(0);
			w.WriteByte(0x01);
			w.WriteUInt32(questId);
			w.WriteUInt32(1);
			w.WriteUInt32(questId);
			w.WriteUInt32(state);
			w.WriteUInt32(0);
			w.WriteUInt32(3);
			w.WriteUInt32(0x412);
			foreach (var rec in textRefs) // title, summary, areaTitle, region, map
				w.WriteArray(rec);
			// Objective/stat run.
			w.WriteUInt32(1);
			w.WriteUInt32(areaId);
			w.WriteUInt32(questId);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0x50);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0x64);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0x7E2);
			w.WriteUInt32(1);
			w.WriteUInt32(0x0C);
			w.WriteUInt32(playerHandle);
			w.WriteUInt32(questId);
			w.WriteUInt32(goalId);
			w.WriteUInt32(1);
			w.WriteUInt32(goalId);
			w.WriteUInt32(1);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteUInt64(acceptTime);
			w.WriteUInt32(0);
			w.WriteUInt32(mapId);
			w.WriteUInt32(markerX);
			w.WriteUInt32(markerY);
			w.WriteUInt32(0);
			w.WriteUInt32(0);
			w.WriteByte(0x00); // trailing u16 before the objective record
			w.WriteByte(0x00);
			w.WriteArray(objectiveText);
			w.WriteArray(objectiveLocalized);
			w.WriteByte(0x00);
			return w.ToArray();
		}

		// QuestGoal (33 B): [subop][ack][playerHandle][0x01][u32 questId][u32 goalId][u32 count][0][0].
		public static byte[] BuildQuestGoal(uint playerHandle, uint questId, uint goalId, uint count)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.QuestGoalIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x01)
				.WriteUInt32(questId)
				.WriteUInt32(goalId)
				.WriteUInt32(count)
				.WriteUInt32(0) // trailing [0][0]
				.WriteUInt32(0)
				.ToArray();
		}

		// QuestIIR (29 B): [subop][ack][playerHandle][0][0x01 marker][u32 state][u32 questId][0].
		public static byte[] BuildQuestIir(uint playerHandle, uint questId, uint state)
		{
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.QuestIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteUInt32(0) // [12..16] = 0
				.WriteByte(0x01)
				.WriteUInt32(state)
				.WriteUInt32(questId)
				.WriteUInt32(0) // [25..29] = 0
				.ToArray();
		}

		// QuestJournal: the whole J-panel quest list.
		//   [01][u32 areaId] <3 area-header records> "RRL3" [00000000][u32 count]
		//   per entry: [u32 index(1-based)][u32 questId][u32 chapter] <title> <subLine>
		//   [trailing tracked-quest block]
		// `areaHeader` is the 3 region/zone-title records (cat 50300 + two cat 52000 map/region names).
		// `trailing` is the "currently tracked quest" pointer block - pass BuildTrackedQuestPointer() (the typed
		// no-track form; the server doesn't drive quest tracking).
		public static byte[] BuildQuestJournal(uint playerHandle, uint areaId, IReadOnlyList<byte[]> areaHeader, IReadOnlyList<JournalEntry> entries, byte[] trailing)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestJournalIIR, playerHandle);
			w.WriteByte(0x01);
			w.WriteUInt32(areaId);
			foreach (var rec in areaHeader)
				w.WriteArray(rec);
			w.WriteArray(Encoding.ASCII.GetBytes(RrlMagic));
			w.WriteUInt32(0);
			w.WriteUInt32((uint)entries.Count);
			for (var i = 0; i < entries.Count; i++)
			{
				w.WriteUInt32((uint)(i + 1)); // 1-based index
				w.WriteUInt32(entries[i].QuestId);
				w.WriteUInt32(ChapterTag);
				w.WriteArray(entries[i].TitleRecord);
				w.WriteArray(entries[i].SubLineRecord);
			}

			w.WriteArray(trailing);
			return w.ToArray();
		}

		// The active-quest LIST (0x504B7924) that POPULATES the J-panel. WITHOUT this the client never shows an
		// accepted quest in the log even though it received the per-quest QuestFullUpdate detail (the detail alone is
		// not enough - decoded from randochar frame 687 / strickenbla frame 3086). Retail sends it at every grant,
		// listing ALL the player's active quests (tracked quest first), plus the current area's journal-title slot.
		// Wire (subopcode onward):
		//   [subop][ack][u32 player] 01 [u32=1] [u32 areaId] [u32 trackedQuestId] [u32 count]
		//   count × { u32 index(1-based); u32 questId }
		//   <areaTitle text slot: 00 <len> <remoteformat areaId/50300>>
		//   [u32=1] [10 zero bytes]
		public static byte[] BuildQuestLogList(uint playerHandle, uint areaId, uint trackedQuestId, IReadOnlyList<uint> questIds, byte[] areaTitleRecord)
		{
			var w = new FaolanStream();
			WriteHeader(w, (uint)GameServer0X2000Opcodes.QuestLogListIIR, playerHandle);
			w.WriteByte(0x01);
			w.WriteUInt32(1);
			w.WriteUInt32(areaId);
			w.WriteUInt32(trackedQuestId);
			w.WriteUInt32((uint)questIds.Count);
			for (var i = 0; i < questIds.Count; i++)
			{
				w.WriteUInt32((uint)(i + 1));
				w.WriteUInt32(questIds[i]);
			}

			w.WriteArray(areaTitleRecord);
			w.WriteUInt32(1);
			for (var i = 0; i < 10; i++)
				w.WriteByte(0x00);
			return w.ToArray();
		}

		// The "currently tracked quest" pointer block that closes a QuestJournal: [u32 trackedQuestId][list A][list B],
		// each list = [u32 (count+1)*0x3F1 framing][u32 count][entries]. Decoded byte-exact from every captured
		// single-quest journal, which all carry the NO-TRACK state (trackedQuestId 0, both lists empty). The server does
		// not drive quest TRACKING - the client picks which quest to follow - so this emits that no-track form from typed
		// fields instead of a hex blob. (Pinning the populated tracked form needs a track-a-quest capture; #193.)
		public static byte[] BuildTrackedQuestPointer(uint trackedQuestId = 0)
		{
			var w = new FaolanStream();
			w.WriteUInt32(trackedQuestId);
			w.WriteUInt32(ListFrame);
			w.WriteUInt32(0); // list A (empty)
			w.WriteUInt32(ListFrame);
			w.WriteUInt32(0); // list B (empty)
			return w.ToArray();
		}

		public static byte[] Localized(int id, int category, string flags, string key)
		{
			return TextRecord($"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >");
		}

		// One <remoteformat>/<localized> text slot:  00 <u8 length> <ascii bytes>.  `key` is passed
		// pre-escaped (it already contains entities like &#36; in the capture).
		public static byte[] RemoteFormat(int id, int category, string key)
		{
			return TextRecord($"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>");
		}

		// The inverse of BuildQuestGoal: recovers (playerHandle, questId, goalId, count) from a 33-byte QuestGoal body.
		// Validates the sub-opcode, ack TypeTag, the 0x01 marker and the trailing zero words. Proven by the QuestTests
		// round-trip (#87 quest-packets codec consolidation).
		public static bool TryParseQuestGoal(ReadOnlySpan<byte> body, out uint playerHandle, out uint questId, out uint goalId, out uint count)
		{
			playerHandle = 0;
			questId = 0;
			goalId = 0;
			count = 0;
			if (body.Length != 33)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.QuestGoalIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;
			if (body[12] != 0x01)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(25, 4)) != 0 || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(29, 4)) != 0)
				return false;

			playerHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			questId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4));
			goalId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(17, 4));
			count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(21, 4));
			return true;
		}

		// The inverse of BuildQuestIir: recovers (playerHandle, state, questId) from a 29-byte QuestIIR body. Validates
		// the sub-opcode, ack TypeTag, the zeroed word at [12..16], the 0x01 marker and the trailing zero word.
		public static bool TryParseQuestIir(ReadOnlySpan<byte> body, out uint playerHandle, out uint questId, out uint state)
		{
			playerHandle = 0;
			questId = 0;
			state = 0;
			if (body.Length != 29)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.QuestIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(12, 4)) != 0)
				return false;
			if (body[16] != 0x01)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(25, 4)) != 0)
				return false;

			playerHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			state = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(17, 4));
			questId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(21, 4));
			return true;
		}

		private static byte[] TextRecord(string xml)
		{
			var ascii = Encoding.ASCII.GetBytes(xml);
			if (ascii.Length > 0xFF)
				throw new ArgumentException($"text record too long ({ascii.Length} B): {xml}");

			var b = new byte[2 + ascii.Length];
			b[0] = 0x00;
			b[1] = (byte)ascii.Length;
			ascii.CopyTo(b, 2);
			return b;
		}

		private static void WriteHeader(FaolanStream w, uint subOpcode, uint playerHandle)
		{
			w.WriteUInt32(subOpcode);
			w.WriteUInt32(EntityClass.Character);
			w.WriteUInt32(playerHandle);
		}

		// One line in the journal list: the quest id plus its title (cat 50301) and list sub-line (cat 50303).
		public readonly record struct JournalEntry(uint QuestId, byte[] TitleRecord, byte[] SubLineRecord);
	}
}

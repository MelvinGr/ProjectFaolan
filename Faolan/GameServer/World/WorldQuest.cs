using Faolan.Core.Network;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// Pure quest objective-tracking rules (docs/archive/quests-and-combat.md) - testable without the DB/network.
	public static class QuestProgress
	{
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
		public const uint QuestGoalSubOpcode = (uint)GameServer0X2000RespondsOpcodes.QuestGoalIIR; // QuestGoalIIR_c - objective progress
		public const uint QuestIirSubOpcode = (uint)GameServer0X2000RespondsOpcodes.QuestIIR; // QuestIIR_c - add/update/remove a quest in the journal

		// Quest state codes (from the capture). On completion retail sends QuestIIR twice for the quest: state 2
		// (active/update) then state 4 (completed) - see the Stihetfv compeltefirstquest capture (13479 idx858/860).
		public const uint StateActive = 2;
		public const uint StateCompleted = 4;

		// The QuestFullUpdate (0x63268258) carries its OWN per-quest "state/category" field (the u32 after the second
		// questId) - a DIFFERENT enum from the QuestIIR StateActive above. It is CONSTANT per quest across its whole
		// lifecycle (every captured 13479 full-update uses 3, on grant and on every re-display: frames 687/8876/9947/
		// 10129), so it is a quest property, not a dynamic state. Passing the QuestIIR value (2) here makes the client
		// silently DROP the full-update (journal stays empty) - the bug behind the dialogue-grant not rendering.
		// TODO: harvest this per quest into QuestDefinition; 3 is the captured value for Casilda's 13479 (and the only
		// value observed for active quests so far - a different quest 0x..27 used 1).
		public const uint FullUpdateStateActive = 3;
		public const uint TypeTagAck = EntityClass.Character;

		// The Tortage beach world-entry snapshot (0x737A6DF9) embeds quest 10238 ("Shipwrecked") in its tails, with its
		// FULL multi-objective journal record, for every character (snapshot_embeds_strie_quest). The snapshot is the
		// authoritative delivery for it - the server must NOT re-send a (thinner, single-objective) QuestFullUpdate for
		// it on login, or it clobbers the snapshot's richer entry. Persisted via EnsureBeachStarterQuest; skipped in
		// RestorePlayerQuests.
		public const uint BeachStarterQuestId = 10238;

		// QuestGoal (33 B): [subop][ack][playerHandle][0x01][u32 questId][u32 goalId][u32 count][0][0].
		public static byte[] BuildQuestGoal(uint playerHandle, uint questId, uint goalId, uint count)
		{
			return new FaolanStream()
				.WriteUInt32(QuestGoalSubOpcode)
				.WriteUInt32(TypeTagAck)
				.WriteUInt32(playerHandle)
				.WriteByte(0x01)
				.WriteUInt32(questId)
				.WriteUInt32(goalId)
				.WriteUInt32(count)
				.WriteUInt32(0) // trailing [0][0]
				.WriteUInt32(0)
				.ToArray();
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
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != QuestGoalSubOpcode)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != TypeTagAck)
				return false;
			if (body[12] != 0x01)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(25, 4)) != 0 ||
			    BinaryPrimitives.ReadUInt32BigEndian(body.Slice(29, 4)) != 0)
				return false;

			playerHandle = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			questId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(13, 4));
			goalId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(17, 4));
			count = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(21, 4));
			return true;
		}

		// QuestIIR (29 B): [subop][ack][playerHandle][0][0x01 marker][u32 state][u32 questId][0].
		public static byte[] BuildQuestIir(uint playerHandle, uint questId, uint state)
		{
			return new FaolanStream()
				.WriteUInt32(QuestIirSubOpcode)
				.WriteUInt32(TypeTagAck)
				.WriteUInt32(playerHandle)
				.WriteUInt32(0) // [12..16] = 0
				.WriteByte(0x01)
				.WriteUInt32(state)
				.WriteUInt32(questId)
				.WriteUInt32(0) // [25..29] = 0
				.ToArray();
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
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != QuestIirSubOpcode)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != TypeTagAck)
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

		// --- Journal CONTENT packets (Captures/scripts/decode_quest.py, byte-exact golden tests) ----------
		//
		// These carry the J-panel text the player reads. Text is NEVER literal: every slot is a
		// <remoteformat .../> or <localized .../> token packed as  00 <u8 len> <ascii>.  The client
		// resolves (id, category) against its own RDB; the `key` attribute is a DETERMINISTIC content
		// hash (identical across characters/sessions/captures), so it is harvested data, not a nonce.
		// Builders emit from the subopcode onward; the 4-byte length prefix is added by the send layer
		// (matching BuildQuestIir/BuildQuestGoal).

		public const uint QuestJournalSubOpcode = (uint)GameServer0X2000RespondsOpcodes.QuestJournalIIR; // the J-panel quest list
		public const uint QuestFullUpdateSubOpcode = (uint)GameServer0X2000RespondsOpcodes.QuestFullUpdateIIR; // one quest's full detail
		public const uint QuestLogListSubOpcode = (uint)GameServer0X2000RespondsOpcodes.QuestLogListIIR; // the active-quest LIST that POPULATES the J-panel
		public const uint AreaTitleCategory = 50300; // category of the area journal-title remoteformat

		// BuildQuestFullUpdate emits [subop 4][ack 4][player 4][u32 0 = 4][0x01 = 1] = 17 bytes before `questId`; the
		// per-quest snapshot-tails content is that output from `questId` onward (used by the snapshot-shape tests).
		public const int QuestFullUpdateContentOffset = 17;

		// The fixed u64 the QuestFullUpdate carries at its `acceptTime` slot inside the snapshot tails (NOT a live
		// timestamp - it is the same constant in the live grant packet and the in-progress login snapshot; decoded as
		// a structural header field, docs/quest-log-handover.md).
		public const ulong SnapshotQuestAcceptTime = 0x000111D354375554UL;
		private const uint ChapterTag = 0x00000BD3; // per-quest grouping word, constant across Tortage quests
		private const string RrlMagic = "RRL3"; // record-list magic that precedes the per-quest entries

		// One <remoteformat>/<localized> text slot:  00 <u8 length> <ascii bytes>.  `key` is passed
		// pre-escaped (it already contains entities like &#36; in the capture).
		public static byte[] RemoteFormat(int id, int category, string key) =>
			TextRecord($"<remoteformat id=\"{id}\" category=\"{category}\" key=\"{key}\" knubot=\"0\"  ></remoteformat>");

		public static byte[] Localized(int id, int category, string flags, string key) =>
			TextRecord($"<localized id=\"{id}\" category=\"{category}\" flags=\"{flags}\" key=\"{key}\" >");

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

		// One line in the journal list: the quest id plus its title (cat 50301) and list sub-line (cat 50303).
		public readonly record struct JournalEntry(uint QuestId, byte[] TitleRecord, byte[] SubLineRecord);

		// QuestJournal: the whole J-panel quest list.
		//   [01][u32 areaId] <3 area-header records> "RRL3" [00000000][u32 count]
		//   per entry: [u32 index(1-based)][u32 questId][u32 chapter] <title> <subLine>
		//   [trailing tracked-quest block]
		// `areaHeader` is the 3 region/zone-title records (cat 50300 + two cat 52000 map/region names).
		// `trailing` is the "currently tracked quest" pointer block (state-dependent; pass the captured
		// no-tracked-quest block 00000000 000003f1 00000000 000003f1 00000000 for a single fresh quest).
		public static byte[] BuildQuestJournal(uint playerHandle, uint areaId, IReadOnlyList<byte[]> areaHeader, IReadOnlyList<JournalEntry> entries, byte[] trailing)
		{
			var w = new List<byte>(256);
			WriteHeader(w, QuestJournalSubOpcode, playerHandle);
			w.Add(0x01);
			WriteU32(w, areaId);
			foreach (var rec in areaHeader)
				w.AddRange(rec);
			w.AddRange(Encoding.ASCII.GetBytes(RrlMagic));
			WriteU32(w, 0);
			WriteU32(w, (uint)entries.Count);
			for (var i = 0; i < entries.Count; i++)
			{
				WriteU32(w, (uint)(i + 1)); // 1-based index
				WriteU32(w, entries[i].QuestId);
				WriteU32(w, ChapterTag);
				w.AddRange(entries[i].TitleRecord);
				w.AddRange(entries[i].SubLineRecord);
			}
			w.AddRange(trailing);
			return w.ToArray();
		}

		// QuestFullUpdate: one quest's full record (description, summary, objective + world marker).
		//   header: [00000000][01][questId][00000001][questId][state][00000000][00000003][0x412]
		//   <title 50301> <summary 50302> <areaTitle 50300> <region 52000> <map 52000>
		//   <stat/objective run carrying areaId, questId, goalId, player, acceptTime, map+x+y marker>
		//   <objective text 50304> <objective localized> [00]
		// The mid-run scalars (0x50/0x64/0x7e2/0x0c ...) are not yet semantically pinned; they are
		// reproduced as a documented template and locked by the golden test.
		public static byte[] BuildQuestFullUpdate(uint playerHandle, uint questId, uint state, uint areaId, IReadOnlyList<byte[]> textRefs, uint goalId, ulong acceptTime, uint mapId, uint markerX, uint markerY, byte[] objectiveText, byte[] objectiveLocalized)
		{
			var w = new List<byte>(512);
			WriteHeader(w, QuestFullUpdateSubOpcode, playerHandle);
			WriteU32(w, 0);
			w.Add(0x01);
			WriteU32(w, questId);
			WriteU32(w, 1);
			WriteU32(w, questId);
			WriteU32(w, state);
			WriteU32(w, 0);
			WriteU32(w, 3);
			WriteU32(w, 0x412);
			foreach (var rec in textRefs) // title, summary, areaTitle, region, map
				w.AddRange(rec);
			// Objective/stat run.
			WriteU32(w, 1);
			WriteU32(w, areaId);
			WriteU32(w, questId);
			WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0);
			WriteU32(w, 0x50);
			WriteU32(w, 0); WriteU32(w, 0);
			WriteU32(w, 0x64);
			WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0);
			WriteU32(w, 0x7E2);
			WriteU32(w, 1);
			WriteU32(w, 0x0C);
			WriteU32(w, playerHandle);
			WriteU32(w, questId);
			WriteU32(w, goalId);
			WriteU32(w, 1);
			WriteU32(w, goalId);
			WriteU32(w, 1);
			WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0); WriteU32(w, 0);
			WriteU64(w, acceptTime);
			WriteU32(w, 0);
			WriteU32(w, mapId);
			WriteU32(w, markerX);
			WriteU32(w, markerY);
			WriteU32(w, 0);
			WriteU32(w, 0);
			w.Add(0x00); // trailing u16 before the objective record
			w.Add(0x00);
			w.AddRange(objectiveText);
			w.AddRange(objectiveLocalized);
			w.Add(0x00);
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
			var w = new List<byte>(64 + questIds.Count * 8 + areaTitleRecord.Length);
			WriteHeader(w, QuestLogListSubOpcode, playerHandle);
			w.Add(0x01);
			WriteU32(w, 1);
			WriteU32(w, areaId);
			WriteU32(w, trackedQuestId);
			WriteU32(w, (uint)questIds.Count);
			for (var i = 0; i < questIds.Count; i++)
			{
				WriteU32(w, (uint)(i + 1));
				WriteU32(w, questIds[i]);
			}

			w.AddRange(areaTitleRecord);
			WriteU32(w, 1);
			for (var i = 0; i < 10; i++)
				w.Add(0x00);
			return w.ToArray();
		}

		private static void WriteHeader(List<byte> w, uint subOpcode, uint playerHandle)
		{
			WriteU32(w, subOpcode);
			WriteU32(w, TypeTagAck);
			WriteU32(w, playerHandle);
		}

		private static void WriteU32(List<byte> w, uint v)
		{
			w.Add((byte)(v >> 24));
			w.Add((byte)(v >> 16));
			w.Add((byte)(v >> 8));
			w.Add((byte)v);
		}

		private static void WriteU64(List<byte> w, ulong v)
		{
			WriteU32(w, (uint)(v >> 32));
			WriteU32(w, (uint)v);
		}
	}
}

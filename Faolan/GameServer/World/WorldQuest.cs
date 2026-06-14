using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Database.Tables;

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
		public const uint QuestGoalSubOpcode = 0x0774C8C1; // QuestGoalIIR_c - objective progress
		public const uint QuestIirSubOpcode = 0x97E02314; // QuestIIR_c - add/update/remove a quest in the journal

		// Quest state codes (from the capture; only "active/added" = 2 is confirmed).
		public const uint StateActive = 2;
		public const uint TypeTagAck = 0x0000C350;

		// QuestGoal (33 B): [subop][ack][playerHandle][0x01][u32 questId][u32 goalId][u32 count][0][0].
		public static byte[] BuildQuestGoal(uint playerHandle, uint questId, uint goalId, uint count)
		{
			var b = new byte[33];
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), QuestGoalSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), TypeTagAck);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), playerHandle);
			b[12] = 0x01;
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(13, 4), questId);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(17, 4), goalId);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(21, 4), count);
			// b[25..33] = 0
			return b;
		}

		// QuestIIR (29 B): [subop][ack][playerHandle][0][0x01 marker][u32 state][u32 questId][0].
		public static byte[] BuildQuestIir(uint playerHandle, uint questId, uint state)
		{
			var b = new byte[29];
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0, 4), QuestIirSubOpcode);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), TypeTagAck);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8, 4), playerHandle);
			// b[12..16] = 0
			b[16] = 0x01;
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(17, 4), state);
			BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(21, 4), questId);
			// b[25..29] = 0
			return b;
		}

		// --- Journal CONTENT packets (Captures/decode_quest.py, byte-exact golden tests) ----------
		//
		// These carry the J-panel text the player reads. Text is NEVER literal: every slot is a
		// <remoteformat .../> or <localized .../> token packed as  00 <u8 len> <ascii>.  The client
		// resolves (id, category) against its own RDB; the `key` attribute is a DETERMINISTIC content
		// hash (identical across characters/sessions/captures), so it is harvested data, not a nonce.
		// Builders emit from the subopcode onward; the 4-byte length prefix is added by the send layer
		// (matching BuildQuestIir/BuildQuestGoal).

		public const uint QuestJournalSubOpcode = 0x79F8C7A1; // the J-panel quest list
		public const uint QuestFullUpdateSubOpcode = 0x63268258; // one quest's full detail
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

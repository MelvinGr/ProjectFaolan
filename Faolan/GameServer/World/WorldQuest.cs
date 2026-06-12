using System;
using System.Buffers.Binary;
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
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Seeds each captured character's quest journal (CharacterQuest rows) from three s2c sources on the 0x2000 carrier,
	// parsed by WorldQuest's inverse codecs (each carries the player handle == character id, see
	// player_handle_equals_character_id, so quests attribute to the character):
	//   - QuestLogList (0x504B7924): the BULK J-panel quest set - the whole log in ONE packet (up to ~22 ids/char), so
	//     this is the primary source of a character's held quests. Its ids are entered Active (the log list carries no
	//     per-entry state).
	//   - QuestIIR (0x97E02314): per-quest STATE events; the last state per (char, quest) WINS over the log-list Active
	//     entry - retail sends a completed quest as Active then Completed, so this is what marks a quest Completed.
	//   - QuestGoal (0x0774C8C1): goal progress count for the matching quest.
	// A char's quest ids are unioned across ALL captures (different sessions hold different quests). No QuestDefinition
	// is required - CharacterQuest.QuestId has no FK - so quests we have no journal-content for still record as held.
	// Idempotent: updates an existing row, else inserts. Only characters already in the DB are touched.
	public static class CaptureQuestImporter
	{
		public static async Task<(int Characters, int Quests)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var stateByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> wire state
			var progressByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> goal count
			var logListByChar = new Dictionary<uint, HashSet<uint>>(); // char -> quest ids from the QuestLogList (the J-panel log)
			foreach (var packet in rawPackets)
				TryRead(packet, stateByChar, progressByChar, logListByChar);

			if (stateByChar.Count == 0 && logListByChar.Count == 0)
				return (0, 0);

			// The full set of characters we saw ANY quest data for (log list and/or state events).
			var allChars = new HashSet<uint>(stateByChar.Keys);
			allChars.UnionWith(logListByChar.Keys);

			var charactersSeeded = 0;
			var questsWritten = 0;
			foreach (var characterId in allChars)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				// Merge the two sources into questId -> state: the log list seeds every held quest Active, then any
				// QuestIIR state (Active/Completed) overrides it.
				var merged = new Dictionary<uint, QuestState>();
				if (logListByChar.TryGetValue(characterId, out var logIds))
					foreach (var id in logIds)
						merged[id] = QuestState.Active;
				if (stateByChar.TryGetValue(characterId, out var states))
					foreach (var (id, wire) in states)
						merged[id] = wire == WorldQuest.StateCompleted ? QuestState.Completed : QuestState.Active;

				var existing = await db.CharacterQuests.Where(q => q.CharacterId == characterId).ToDictionaryAsync(q => q.QuestId);
				var progress = progressByChar.GetValueOrDefault(characterId) ?? new Dictionary<uint, uint>();
				var touched = false;
				foreach (var (questId, state) in merged)
				{
					var goalProgress = progress.GetValueOrDefault(questId);

					if (existing.TryGetValue(questId, out var row))
					{
						row.State = state;
						if (goalProgress > 0)
							row.GoalProgress = goalProgress;
					}
					else
					{
						db.CharacterQuests.Add(new CharacterQuest
						{
							CharacterId = characterId,
							QuestId = questId,
							State = state,
							GoalProgress = goalProgress
						});
					}

					questsWritten++;
					touched = true;
				}

				if (touched)
					charactersSeeded++;
			}

			if (questsWritten > 0)
				await db.SaveChangesAsync();

			logger?.LogInformation("CaptureQuestImporter: quest journals for {Characters} characters, {Quests} quest rows.", charactersSeeded, questsWritten);
			return (charactersSeeded, questsWritten);
		}

		private static void TryRead(byte[] packetBytes, Dictionary<uint, Dictionary<uint, uint>> stateByChar, Dictionary<uint, Dictionary<uint, uint>> progressByChar, Dictionary<uint, HashSet<uint>> logListByChar)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
				if (WorldQuest.TryParseQuestLogList(inner, out var logHandle, out _, out var logIds))
				{
					if (!logListByChar.TryGetValue(logHandle, out var set))
						logListByChar[logHandle] = set = new HashSet<uint>();
					foreach (var id in logIds)
						if (id != 0)
							set.Add(id);
					return;
				}

				if (WorldQuest.TryParseQuestIir(inner, out var handle, out var questId, out var state))
				{
					if (questId == 0)
						return;

					(stateByChar.TryGetValue(handle, out var m)
						? m
						: stateByChar[handle] = new Dictionary<uint, uint>())[questId] = state;
				}
				else if (WorldQuest.TryParseQuestGoal(inner, out handle, out questId, out _, out var count))
				{
					if (questId == 0)
						return;

					(progressByChar.TryGetValue(handle, out var m)
						? m
						: progressByChar[handle] = new Dictionary<uint, uint>())[questId] = count;
				}
			}
			catch
			{
				// not a parseable quest packet
			}
		}
	}
}

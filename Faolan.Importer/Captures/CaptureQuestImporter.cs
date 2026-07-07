using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Conan;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	// Seeds each captured character's quest journal (CharacterQuest rows) from the s2c QuestIIR / QuestGoal packets
	// (0x2000 carrier; sub-opcodes QuestIIR / QuestGoal, parsed by WorldQuest's inverse codecs). Each carries the
	// player handle (== character id, see player_handle_equals_character_id), so quests attribute to the character.
	// The LAST QuestIIR state per (char, quest) wins - retail sends a completed quest as Active then Completed - and
	// goal progress comes from the matching QuestGoal. Idempotent: updates an existing row, else inserts. Only
	// characters already in the DB are touched (CaptureCharacterImporter materializes them first).
	public static class CaptureQuestImporter
	{
		public static async Task<(int Characters, int Quests)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger logger = null)
		{
			var stateByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> wire state
			var progressByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> goal count
			foreach (var packet in rawPackets)
				TryRead(packet, stateByChar, progressByChar);

			if (stateByChar.Count == 0)
				return (0, 0);

			var charactersSeeded = 0;
			var questsWritten = 0;
			foreach (var (characterId, quests) in stateByChar)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				var existing = await db.CharacterQuests.Where(q => q.CharacterId == characterId).ToDictionaryAsync(q => q.QuestId);
				var progress = progressByChar.GetValueOrDefault(characterId) ?? new Dictionary<uint, uint>();
				var touched = false;
				foreach (var (questId, wireState) in quests)
				{
					var state = wireState == WorldQuest.StateCompleted
						? QuestState.Completed
						: QuestState.Active;
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

		private static void TryRead(byte[] packetBytes, Dictionary<uint, Dictionary<uint, uint>> stateByChar, Dictionary<uint, Dictionary<uint, uint>> progressByChar)
		{
			try
			{
				var packet = new ConanPacket(packetBytes);
				if (packet.Opcode != 0x2000)
					return;

				var inner = packet.Body.ReadArrayUInt32Length();
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Seeds each captured character's quest journal (CharacterQuest rows) from the s2c quest sources on the PassBlob
	///     carrier, parsed by the quest codecs.
	/// </summary>
	/// <remarks>
	///     Each source carries the player handle, which equals the character id, so quests attribute to the character:
	///     the QuestLogList (MainQuestUpdate) is the bulk J-panel quest set - the whole log in one packet, the primary
	///     source of a character's held quests; its ids are entered Active (the log list carries no per-entry state).
	///     QuestIIR carries per-quest state events; the last state per (character, quest) wins over the log-list Active
	///     entry - retail sends a completed quest as Active then Completed, so this is what marks a quest Completed.
	///     QuestGoal carries the goal update kind for the matching quest (met / partial / closed) - not a count.
	///     The FullChar snapshot's Quest_c journal region (SnapshotJournalReader) is the character's J-panel at login,
	///     one record per held quest, and is authoritative for what a character actually holds.
	///     The FullChar snapshot also embeds the character's completed-quest log (one category-50301 title record each);
	///     these are entered Completed. The snapshot is addressed to the player, so Receiver.Instance is the character
	///     id. This is the only source of the completed archive - the incremental packets only ever carry the
	///     currently-held quests.
	///     A character's quest ids are unioned across all captures (different sessions hold different quests). No
	///     QuestDefinition is required - CharacterQuest.QuestId has no FK - so quests with no journal content still
	///     record as held. Idempotent: updates an existing row, else inserts. Only characters already in the DB are
	///     touched.
	/// </remarks>
	public static class CaptureQuestImporter
	{
		private const uint SnapshotSubOpcode = FullPlayerCharacterCodec.Opcode;

		public static async Task<(int Characters, int Quests)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var stateByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> wire state
			// char -> questId -> the last QuestGoalIIR update seen. The packet carries an update kind, not a running
			// count, so this says whether the goal is met - not how far along it is.
			var goalUpdateByChar = new Dictionary<uint, Dictionary<uint, QuestGoalUpdate>>();
			var logListByChar = new Dictionary<uint, HashSet<uint>>(); // char -> quest ids from the QuestLogList (the J-panel log)
			var completedByChar = new Dictionary<uint, HashSet<uint>>(); // char -> completed quest ids from the snapshot log
			var journalByChar = new Dictionary<uint, HashSet<uint>>(); // char -> held quest ids from the snapshot's Quest_c region
			foreach (var packet in rawPackets)
			{
				TryRead(packet, stateByChar, goalUpdateByChar, logListByChar, completedByChar);

				// The snapshot's Quest_c region is the character's J-panel at login - one record per held quest.
				if (SnapshotJournalReader.TryRead(packet, out var journalChar, out var journal))
				{
					if (!journalByChar.TryGetValue(journalChar, out var held))
						journalByChar[journalChar] = held = [];
					foreach (var record in journal)
						held.Add(record.QuestId);
				}
			}

			if (stateByChar.Count == 0 && logListByChar.Count == 0 && completedByChar.Count == 0 && journalByChar.Count == 0)
				return (0, 0);

			// The full set of characters with any quest data (completed log, active log list, and/or state events).
			var allChars = new HashSet<uint>(stateByChar.Keys);
			allChars.UnionWith(logListByChar.Keys);
			allChars.UnionWith(completedByChar.Keys);
			allChars.UnionWith(journalByChar.Keys);

			var charactersSeeded = 0;
			var questsWritten = 0;
			foreach (var characterId in allChars)
			{
				if (!await db.Characters.AnyAsync(c => c.Id == characterId))
					continue;

				// Merge the sources into questId -> state, least- to most-authoritative (last wins): the snapshot log
				// seeds Completed, the active log list overrides to Active (a re-taken/repeatable quest), then an
				// explicit QuestIIR state event wins.
				var merged = new Dictionary<uint, QuestState>();
				if (completedByChar.TryGetValue(characterId, out var completedIds))
				{
					foreach (var id in completedIds)
						merged[id] = QuestState.Completed;
				}

				// The snapshot journal overrides the completed archive: a quest with a Quest_c record was held at login.
				if (journalByChar.TryGetValue(characterId, out var heldIds))
				{
					foreach (var id in heldIds)
						merged[id] = QuestState.Active;
				}

				if (logListByChar.TryGetValue(characterId, out var logIds))
				{
					foreach (var id in logIds)
						merged[id] = QuestState.Active;
				}

				if (stateByChar.TryGetValue(characterId, out var states))
				{
					foreach (var (id, wire) in states)
					{
						merged[id] = wire == QuestCodec.StateCompleted
							? QuestState.Completed
							: QuestState.Active;
					}
				}

				var existing = await db.CharacterQuests.Where(q => q.CharacterId == characterId).ToDictionaryAsync(q => q.QuestId);
				var goalUpdates = goalUpdateByChar.GetValueOrDefault(characterId) ?? [];
				var touched = false;
				foreach (var (questId, state) in merged)
				{
					// A QuestGoalIIR states a kind, not a count: Satisfied/Advanced mean the goal is met, Progress means
					// it moved by an amount the packet never carries. So only a met goal can be turned into a number
					// here - it becomes the required count (at least 1) - and a partial tick leaves whatever the row
					// already had, rather than inventing a figure.
					var met = goalUpdates.TryGetValue(questId, out var update) && update != QuestGoalUpdate.Progress;

					if (existing.TryGetValue(questId, out var row))
					{
						row.State = state;
						if (met)
							row.GoalProgress = Math.Max(row.GoalRequired, 1u);
					}
					else
					{
						db.CharacterQuests.Add(new CharacterQuest
						{
							CharacterId = characterId,
							QuestId = questId,
							State = state,
							GoalProgress = met ? 1u : 0u
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

		private static void TryRead(byte[] packetBytes, Dictionary<uint, Dictionary<uint, uint>> stateByChar, Dictionary<uint, Dictionary<uint, QuestGoalUpdate>> goalUpdateByChar,
			Dictionary<uint, HashSet<uint>> logListByChar, Dictionary<uint, HashSet<uint>> completedByChar)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Method != (uint)GameAgentMethods.PassBlob)
					return;

				var inner = packet.BodyReader().ReadArrayUInt32Length();

				// The FullChar snapshot embeds the completed-quest journal. Read it typed via
				// SnapshotJournalReader.TryReadCompleted (as CompletedQuestExtractor does): the typed region returns only
				// the completed archive, whereas a scan for category-50301 titles would also sweep up the active
				// journal's titles.
				if (inner.Length >= 4 && inner.ReadU32Be(0) == SnapshotSubOpcode)
				{
					var charId = packet.Receiver.Instance;
					if (charId == 0)
						return;

					if (SnapshotJournalReader.TryReadCompleted(packetBytes, out var completed) && completed != null)
					{
						if (!completedByChar.TryGetValue(charId, out var set))
							completedByChar[charId] = set = [];
						foreach (var area in completed.Areas)
						foreach (var q in area.Quests)
						{
							if (q.QuestId != 0)
								set.Add(q.QuestId);
						}
					}

					return;
				}

				// Dispatch on the record's own sub-opcode rather than trying each codec until one returns true: the
				// codecs' Parse is strict and throws, and "try until one succeeds" would abort the whole packet the
				// moment the first candidate rejected it.
				if (inner.Length < 4)
					return;

				switch (inner.ReadU32Be(0))
				{
					case MainQuestUpdateCodec.Opcode:
					{
						var log = MainQuestUpdateCodec.Parse(inner);
						if (!logListByChar.TryGetValue(log.PlayerHandle, out var set))
							logListByChar[log.PlayerHandle] = set = [];
						foreach (var entry in log.Entries)
						{
							if (entry.QuestId != 0)
								set.Add(entry.QuestId);
						}

						return;
					}

					case QuestCodec.Opcode:
					{
						var quest = QuestCodec.Parse(inner);
						if (quest.QuestId == 0)
							return;

						(stateByChar.TryGetValue(quest.PlayerHandle, out var m)
							? m
							: stateByChar[quest.PlayerHandle] = [])[quest.QuestId] = quest.State;
						return;
					}

					case QuestGoalCodec.Opcode:
					{
						var goal = QuestGoalCodec.Parse(inner);
						if (goal.QuestId == 0)
							return;

						(goalUpdateByChar.TryGetValue(goal.PlayerHandle, out var m)
							? m
							: goalUpdateByChar[goal.PlayerHandle] = [])[goal.QuestId] = goal.Update;
						return;
					}
				}
			}
			catch
			{
				// not a parseable quest packet
			}
		}
	}
}

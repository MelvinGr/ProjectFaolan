using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Protocol;
using Faolan.Importer.Extractors;
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
	//   - the FullChar SNAPSHOT's Quest_c JOURNAL region (SnapshotJournalReader): the character's J-panel at login, one
	//     record per HELD quest. Authoritative for what a character actually holds - it overrides the completed sweep
	//     below, whose regex cannot tell a journal title record from a completed-archive one.
	//   - the FullChar SNAPSHOT (0x737A6DF9, DispatchPassBlob): the login snapshot embeds the character's COMPLETED-quest
	//     log - hundreds of quests for a high-level char, one <remoteformat category=50301> title record each (the
	//     lvl-80 necro's is a 625 KB snapshot with ~1540 of them). These are entered Completed; the snapshot is addressed
	//     to the player, so Receiver.Instance == the character id. This is the only source of the completed archive - the
	//     incremental QuestIIR/log-list packets only ever carry the currently-held quests.
	// A char's quest ids are unioned across ALL captures (different sessions hold different quests). No QuestDefinition
	// is required - CharacterQuest.QuestId has no FK - so quests we have no journal-content for still record as held.
	// Idempotent: updates an existing row, else inserts. Only characters already in the DB are touched.
	public static class CaptureQuestImporter
	{
		private const uint SnapshotSubOpcode = (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR;

		public static async Task<(int Characters, int Quests)> ImportAsync(IDatabaseContext db, IEnumerable<byte[]> rawPackets, ILogger? logger = null)
		{
			var stateByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> wire state
			var progressByChar = new Dictionary<uint, Dictionary<uint, uint>>(); // char -> questId -> goal count
			var logListByChar = new Dictionary<uint, HashSet<uint>>(); // char -> quest ids from the QuestLogList (the J-panel log)
			var completedByChar = new Dictionary<uint, HashSet<uint>>(); // char -> completed quest ids from the snapshot log
			var journalByChar = new Dictionary<uint, HashSet<uint>>(); // char -> HELD quest ids from the snapshot's Quest_c region
			foreach (var packet in rawPackets)
			{
				TryRead(packet, stateByChar, progressByChar, logListByChar, completedByChar);

				// The snapshot's Quest_c region IS the character's J-panel at login - one record per HELD quest. Without
				// it the completed-title regex below swept those same quests into the COMPLETED archive (their title
				// records are byte-identical in form), so a character's real journal was stored inside-out: NecroL80's 27
				// held quests all Completed, and 17 unrelated session-touched quests marked Active.
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

			// The full set of characters we saw ANY quest data for (completed log, active log list, and/or state events).
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

				// The snapshot journal overrides the completed sweep: a quest with a Quest_c record was HELD at login,
				// and its title record is what the regex above just mis-filed as completed.
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
						merged[id] = wire == WorldQuest.StateCompleted
							? QuestState.Completed
							: QuestState.Active;
					}
				}

				var existing = await db.CharacterQuests.Where(q => q.CharacterId == characterId).ToDictionaryAsync(q => q.QuestId);
				var progress = progressByChar.GetValueOrDefault(characterId) ?? [];
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

		private static void TryRead(byte[] packetBytes, Dictionary<uint, Dictionary<uint, uint>> stateByChar, Dictionary<uint, Dictionary<uint, uint>> progressByChar,
			Dictionary<uint, HashSet<uint>> logListByChar, Dictionary<uint, HashSet<uint>> completedByChar)
		{
			try
			{
				var packet = Rpc.Parse(packetBytes);
				if (packet.Opcode != (ushort)GameAgentOpcodes.PassBlob)
					return;

				var inner = packet.BodyReader().ReadArrayUInt32Length();

				// The FullChar snapshot embeds the COMPLETED-quest journal (#390). Read it TYPED via
				// SnapshotJournalReader.TryReadCompleted (as CompletedQuestExtractor does) instead of regex-scanning the
				// whole packet for category-50301 titles: the regex also swept up the ACTIVE journal's titles (the reason
				// journalByChar had to override them back), whereas the typed region returns ONLY the completed archive.
				if (inner.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(inner.AsSpan(0, 4)) == SnapshotSubOpcode)
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

				if (WorldQuest.TryParseQuestLogList(inner, out var logHandle, out _, out var logIds))
				{
					if (!logListByChar.TryGetValue(logHandle, out var set))
						logListByChar[logHandle] = set = [];
					foreach (var id in logIds)
					{
						if (id != 0)
							set.Add(id);
					}

					return;
				}

				if (WorldQuest.TryParseQuestIir(inner, out var handle, out var questId, out var state))
				{
					if (questId == 0)
						return;

					(stateByChar.TryGetValue(handle, out var m)
						? m
						: stateByChar[handle] = [])[questId] = state;
				}
				else if (WorldQuest.TryParseQuestGoal(inner, out handle, out questId, out _, out var count))
				{
					if (questId == 0)
						return;

					(progressByChar.TryGetValue(handle, out var m)
						? m
						: progressByChar[handle] = [])[questId] = count;
				}
			}
			catch
			{
				// not a parseable quest packet
			}
		}
	}
}

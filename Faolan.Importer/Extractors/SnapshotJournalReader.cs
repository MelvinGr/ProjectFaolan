using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Reads the Quest_c JOURNAL region out of a captured FullChar snapshot (0x737A6DF9) - the character's live
	// J-panel at login, one record per held quest, each carrying the quest's title/summary/area/region text refs and
	// its record scalars.
	//
	// This is the authoritative source for two things nothing else in the corpus provides:
	//   - which quests a character actually holds (the incremental QuestLogList/QuestIIR packets only show quests
	//     touched during that capture session);
	//   - the journal content for those quests, so their definitions can be generated rather than replayed.
	//
	// Neither is recoverable by scanning the snapshot for `<remoteformat id="N" category="50301">` title records:
	// the Quest_c records of the ACTIVE journal carry title records in exactly that form, so a text scan sweeps a
	// character's held quests into the completed archive wholesale. The region has to be parsed, per record, with
	// the objectives interleaved as the format states them.
	public static class SnapshotJournalReader
	{

		// True when the packet is a 0x2000 carrier at all - a cheap pre-filter for callers scanning every packet.
		public static bool IsCarrier(uint opcode)
		{
			return opcode == (uint)GameAgentMethods.PassBlob;
		}

		// The journal records a snapshot packet carries, with the character they belong to. Returns false for any
		// packet that is not a parseable player snapshot.
		public static bool TryRead(byte[] packet, out uint characterId, out IReadOnlyList<QuestcRecord> records)
		{
			return TryRead(packet, out characterId, out records, out _);
		}

		// As above, but also surfaces the quest index region that follows the journal. The index carries the per-quest
		// goal-table word and goal-step ids, which the Quest_c record itself does not - both are needed before a quest
		// can ride the login snapshot.
		public static bool TryRead(byte[] packet, out uint characterId, out IReadOnlyList<QuestcRecord> records, out QuestIndexRegion? index)
		{
			characterId = 0;
			records = [];
			index = null;

			var record = CapturedRecords.Record(packet);
			if (record.Length < 16 || record.ReadU32Be(0) != FullPlayerCharacterCodec.Opcode)
				return false;

			try
			{
				// The full record: the journal lives in the sub-class (tails), so the root-only reader will not do.
				var parts = FullPlayerCharacterCodec.Parse(record);
				characterId = parts.Instance; // the snapshot is addressed to its own player
				records = parts.SubClass.Quests.Records;
				// The quest index follows the journal and parses for every held-quest snapshot, so surface it:
				// QuestLineExtractor reads the per-block area 4CCs from it - areas an active quest reaches but no
				// completed journal shows.
				index = parts.SubClass.QuestIndex;
				return records.Count > 0;
			}
			catch
			{
				// truncated capture, or a snapshot shape we cannot split - the caller falls back to its other sources
				return false;
			}
		}

		// The COMPLETED-quest journal a snapshot packet carries - the area-grouped region after the active
		// journal. Returns false for any packet that is not a parseable player snapshot, or that carries no
		// completed quests (a fresh character has none, which is why the region is absent from the beach template).
		public static bool TryReadCompleted(byte[] packet, out CompletedQuestJournalRegion? completed)
		{
			completed = null;

			var record = CapturedRecords.Record(packet);
			if (record.Length < 16 || record.ReadU32Be(0) != FullPlayerCharacterCodec.Opcode)
				return false;

			try
			{
				completed = FullPlayerCharacterCodec.Parse(record).SubClass.CompletedQuests;
				return completed is { Areas.Count: > 0 };
			}
			catch
			{
				return false;
			}
		}

	}
}

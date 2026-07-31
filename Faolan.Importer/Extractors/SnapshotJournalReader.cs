using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Importer.Extractors
{
	// Reads the Quest_c JOURNAL region out of a captured FullChar snapshot (0x737A6DF9) - the character's live
	// J-panel at login, one record per HELD quest, each carrying the quest's title/summary/area/region text refs and
	// its record scalars.
	//
	// This is the authoritative source for two things nothing else in the corpus provides:
	//   - which quests a character actually HOLDS (the incremental QuestLogList/QuestIIR packets only show quests
	//     touched during that capture session);
	//   - the journal CONTENT for those quests, so their definitions can be generated rather than replayed.
	//
	// Both were previously lost. CaptureQuestImporter regex-scanned the whole snapshot for
	// `<remoteformat id="N" category="50301">` title records and entered every hit as COMPLETED - but the Quest_c
	// records carry title records in exactly that form, so a character's ACTIVE journal was swept into the completed
	// archive wholesale (NecroL80: all 27 of her held quests stored as Completed, while the 17 marked Active were
	// incidental session packets). QuestDefinitionExtractor, meanwhile, only read the QuestFullUpdate/QuestJournal
	// packets, which covered 2 of her 27.
	//
	// Reading this region only became possible on 2026-07-20: QuestcRegion threw on any progressed journal until the
	// per-record objective interleaving was fixed (Gitea #384).
	public static class SnapshotJournalReader
	{

		// True when the packet is a 0x2000 carrier at all - a cheap pre-filter for callers scanning every packet.
		public static bool IsCarrier(uint opcode)
		{
			return opcode == (uint)GameAgentOpcodes.PassBlob;
		}

		// The journal records a snapshot packet carries, with the character they belong to. Returns false for any
		// packet that is not a parseable player snapshot.
		public static bool TryRead(byte[] packet, out uint characterId, out IReadOnlyList<QuestcRecord> records)
		{
			return TryRead(packet, out characterId, out records, out _);
		}

		// As above, but also surfaces the quest INDEX region that follows the journal. The index carries the per-quest
		// goal-table word and goal-step ids, which the Quest_c record itself does not - both are needed before a quest
		// can ride the login snapshot (#389).
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
				// The full record: the journal lives in the SUB-CLASS (tails), so the root-only reader will not do.
				var parts = FullPlayerCharacterCodec.Parse(record);
				characterId = parts.Instance; // the snapshot is addressed to its own player
				records = parts.SubClass.Quests.Records;
				// The quest index follows the journal; since #391 it parses for every held-quest snapshot, so surface
				// it (QuestAreaExtractor reads the per-block area 4CCs from it - areas an ACTIVE quest reaches but no
				// completed journal shows).
				index = parts.SubClass.QuestIndex;
				return records.Count > 0;
			}
			catch
			{
				// truncated capture, or a snapshot shape we cannot split - the caller falls back to its other sources
				return false;
			}
		}

		// The COMPLETED-quest journal a snapshot packet carries (#390) - the area-grouped region after the active
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

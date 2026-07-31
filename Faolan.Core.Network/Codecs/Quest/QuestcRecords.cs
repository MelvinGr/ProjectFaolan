using System;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	// VALUE factory for a Quest_c journal record: the construct-don't-replay generator for the quest region (#186),
	// proven byte-exact for 10238 by QuestcGenerationTests. Not a codec - it maps DB rows to the record
	// QuestcRecordCodec encodes, and per #530 rule 1 that mapping is a neighbour rather than an overload on the
	// codec's surface (#533 owns row<->value mapping proper).
	public static class QuestcRecords
	{
		// One journal record from a QuestDefinition: its 5 header text refs + areaId, plus the per-quest scalars that
		// are carried from the capture rather than derived (the Snapshot* columns). The numeric objective lists are
		// left empty - the objective TEXT rides in the strings; see docs/re/fullplayer-snapshot-reader.md.
		public static QuestcRecord ForQuest(QuestDefinition def, uint state)
		{
			ArgumentNullException.ThrowIfNull(def);

			return new QuestcRecord
			{
				QuestId = def.QuestId,
				State = state,
				Field114 = 0,
				// The SnapshotUnknown118 column feeds field118 despite its name - the DB column keeps the old
				// (mislabeled) name to avoid a rename migration.
				Field118 = def.SnapshotUnknown118,
				// Carried through from the capture, meaning unknown. This used to emit a constant 0x412 for EVERY
				// quest, which is only right for the beach journal - captured records vary (NecroL80's run 2, 10, ...).
				// Substituting a constant here is the same mistake as defaulting the vanity mask: the wire must carry
				// what retail carried.
				Unknown14 = def.SnapshotUnknown14,
				Title = Slot(QuestTextSlot.Title),
				Summary = Slot(QuestTextSlot.Summary),
				Area = Slot(QuestTextSlot.AreaTitle),
				Region1 = Slot(QuestTextSlot.RegionName),
				Region2 = Slot(QuestTextSlot.MapName),
				FieldEc = def.SnapshotUnknownEc,
				AreaId = def.AreaId,
				Field2c = def.QuestId,
				Field11c = 0,
				Accept = 0,
				Field28 = def.SnapshotUnknown28,
				FieldF0 = def.SnapshotUnknownF0,
				FieldF8 = def.SnapshotUnknownF8
			};

			string Slot(QuestTextSlot slot)
			{
				var t = def.TextRefs.FirstOrDefault(x => x.Slot == slot) ?? throw new InvalidOperationException($"Quest {def.QuestId}: missing text ref slot {slot}");
				return QuestTextTokens.RemoteFormat(t.RefId, t.Category, t.KeyText);
			}
		}
	}
}

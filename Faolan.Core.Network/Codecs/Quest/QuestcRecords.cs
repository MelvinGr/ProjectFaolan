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
		public static QuestcRecord ForQuest(QuestDefinition def, uint level)
		{
			ArgumentNullException.ThrowIfNull(def);

			return new QuestcRecord
			{
				QuestId = def.QuestId,
				Level = level,
				Field114 = 0,
				// The Field118 column feeds field118 despite its name - the DB column keeps the old
				// (mislabeled) name to avoid a rename migration.
				Field118 = def.Field118,
				// Carried through from the capture, meaning unknown. This used to emit a constant 0x412 for EVERY
				// quest, which is only right for the beach journal - captured records vary (NecroL80's run 2, 10, ...).
				// Substituting a constant here is the same mistake as defaulting the vanity mask: the wire must carry
				// what retail carried.
				QuestLineFlags = def.QuestLineFlags,
				Title = Slot(QuestTextSlot.Title),
				Summary = Slot(QuestTextSlot.Summary),
				Area = Slot(QuestTextSlot.QuestLineTitle),
				Region1 = Slot(QuestTextSlot.RegionName),
				Region2 = Slot(QuestTextSlot.MapName),
				FieldEc = def.FieldEc,
				QuestLineId = def.QuestLineId,
				// @+0x2c is the series HEAD, not this quest's id (#566) - equal for a standalone quest, an earlier
				// step for a chain member. Falls back to the own id when the column was never harvested, which is
				// both the standalone answer and what we emitted before the column existed.
				Field2c = def.ChainHeadQuestId == 0
					? def.QuestId
					: def.ChainHeadQuestId,
				Field11c = 0,
				MoneyReward = def.MoneyReward,
				XpReward = def.XpReward,
				FieldF0 = def.FieldF0,
				FieldF8 = def.FieldF8
			};

			string Slot(QuestTextSlot slot)
			{
				var t = def.TextRefs.FirstOrDefault(x => x.Slot == slot) ?? throw new InvalidOperationException($"Quest {def.QuestId}: missing text ref slot {slot}");
				return QuestTextTokens.RemoteFormat(t.RefId, t.Category, t.KeyText);
			}
		}
	}
}

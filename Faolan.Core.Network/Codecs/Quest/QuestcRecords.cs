using System;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     Value factory for a Quest_c journal record: the generator for the quest region, proven byte-exact for quest
	///     10238 by QuestcGenerationTests. Not a codec - it maps database rows onto the record
	///     <see cref="QuestcRecordCodec" /> encodes, and that mapping is a neighbour rather than an overload on the codec's
	///     surface.
	/// </summary>
	public static class QuestcRecords
	{
		/// <summary>
		///     One journal record from a QuestDefinition: its five header text refs and quest-line id, plus the per-quest
		///     scalars carried from the capture rather than derived. The numeric objective lists are left empty - the
		///     objective text rides in the strings; see docs/re/fullplayer-snapshot-reader.md.
		/// </summary>
		/// <remarks>
		///     <paramref name="rewards" /> is the record's four reward lists (QuestRewardLists.ForQuest); null builds a
		///     record with all four empty, which is what a quest with no reward data of either kind carries.
		/// </remarks>
		public static QuestcRecord ForQuest(QuestDefinition def, uint level, QuestRewardLists? rewards = null)
		{
			ArgumentNullException.ThrowIfNull(def);
			rewards ??= QuestRewardLists.Empty;

			return new QuestcRecord
			{
				QuestId = def.QuestId,
				Level = level,
				BandTolerance = 0,
				BandLevel = def.BandLevel,
				// Carried through from the capture, meaning unknown. It is not a constant: captured records vary, and a
				// fixed value is only right for the beach journal.
				QuestLineFlags = def.QuestLineFlags,
				Title = Slot(QuestTextSlot.Title),
				Summary = Slot(QuestTextSlot.Summary),
				Area = Slot(QuestTextSlot.QuestLineTitle),
				Region1 = Slot(QuestTextSlot.RegionName),
				Region2 = Slot(QuestTextSlot.MapName),
				FieldEc = def.FieldEc,
				QuestLineId = def.QuestLineId,
				// @+0x2c is the series head, not this quest's id: equal for a standalone quest, an earlier step for a
				// chain member. It falls back to the quest's own id when the column was never harvested, which is the
				// standalone answer.
				Field2c = def.ChainHeadQuestId == 0
					? def.QuestId
					: def.ChainHeadQuestId,
				Field11c = 0,
				MoneyReward = def.MoneyReward,
				XpReward = def.XpReward,
				FieldF0 = def.FieldF0,
				FieldF8 = def.FieldF8,
				Obj1 = rewards.Obj1,
				Obj2 = rewards.Obj2,
				Obj3 = rewards.Obj3,
				Obj4 = rewards.Obj4
			};

			string Slot(QuestTextSlot slot)
			{
				var t = def.TextRefs.FirstOrDefault(x => x.Slot == slot) ?? throw new InvalidOperationException($"Quest {def.QuestId}: missing text ref slot {slot}");
				return QuestTextTokens.RemoteFormat(t.RefId, t.Category, t.KeyText);
			}
		}
	}
}

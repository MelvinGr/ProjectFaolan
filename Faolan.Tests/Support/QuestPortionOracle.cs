using System;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;

namespace Faolan.Tests.Support
{
	// The single-quest quest portion, used as the byte-exact oracle for the multi-quest
	// FullCharSnapshotSubClass.BuildQuestPortion: for one held quest the two are byte-identical (proven by
	// BuildQuestPortionTests.BuildSingleQuestPortion_EqualsMultiQuestForm). Lives in the test project because the
	// live snapshot path only ever uses the multi-quest form - this is a test-only convenience seam.
	public static class QuestPortionOracle
	{
		public static byte[] BuildSingle(QuestDefinition def, uint state, uint instance)
		{
			ArgumentNullException.ThrowIfNull(def);

			var record = QuestcRecord.BuildSingle(def, state, def.SnapshotUnknown118, def.SnapshotUnknownEc, def.SnapshotUnknownF0, def.SnapshotUnknownF8);
			record.Objectives = QuestObjectiveRegion.Build(def, instance);
			var quests = new QuestcRegion
			{
				Records =
				{
					record
				}
			};
			var index = QuestIndexRegion.Build([def], def);

			// The empty completed-journal frame (0x3F1) always sits between the active journal and the index (#391).
			byte[] portion = [.. quests.ToArray(), .. CompletedQuestJournalRegion.Build([]).ToArray(), .. index.ToArray()];
			if (portion.Length != FullCharSnapshotSubClass.QuestPortionLength)
				throw new InvalidOperationException($"generated quest portion is {portion.Length} B, expected {FullCharSnapshotSubClass.QuestPortionLength}");

			return portion;
		}
	}
}

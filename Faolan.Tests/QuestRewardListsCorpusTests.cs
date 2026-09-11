using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// Construct-don't-replay for the journal record's four reward lists. Every captured Quest_c record in the
	// corpus slice - the login snapshot's and the live QuestFullUpdate's - is rebuilt from the shipped rows
	// (QuestRewardOptions for the quest's own set, QuestLineRewards for the line's, Items for the keys and levels)
	// and must come out element-exact: same templates in the same order with the same key, level and quantity.
	//
	// Breadth by construction, like the other snapshot round-trips: the beach starter carries no reward list at
	// all, so a golden would gate nothing, while the slice holds ~5,000 elements over ~180 quests.
	public class QuestRewardListsCorpusTests
	{
		[Fact]
		public async Task EveryCapturedRewardList_RebuildsFromTheDatabase()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var defs = await db.QuestDefinitions.AsNoTracking().Include(q => q.Rewards).Include(q => q.Line.Rewards).AsSplitQuery().ToDictionaryAsync(q => q.QuestId, ct);
			var items = await db.Items.AsNoTracking().ToDictionaryAsync(i => i.Id, ct);
			Item? ItemOf(uint id) => items.GetValueOrDefault(id);

			int records = 0, withLists = 0, exact = 0, unknownQuest = 0;
			var mismatches = new List<string>();

			foreach (var (source, record) in Records())
			{
				records++;
				if (!defs.TryGetValue(record.QuestId, out var def))
				{
					unknownQuest++;
					continue;
				}

				var captured = new[] { record.Obj1, record.Obj2, record.Obj3, record.Obj4 };
				if (captured.All(l => l.Count == 0) && def.Rewards.Count == 0 && def.Line.Rewards.Count == 0)
					continue; // nothing to build and nothing captured - not a case

				withLists++;
				var instance = captured.SelectMany(l => l).Select(e => e.Instance).FirstOrDefault();
				var built = QuestRewardLists.ForQuest(def, def.Line, instance, ItemOf);
				var ours = new[] { built.Obj1, built.Obj2, built.Obj3, built.Obj4 };

				var diff = new List<string>();
				for (var i = 0; i < 4; i++)
				{
					var a = captured[i].Select(Describe).ToList();
					var b = ours[i].Select(Describe).ToList();
					if (!a.SequenceEqual(b))
						diff.Add($"obj{i + 1} wire [{string.Join(" ", a)}] built [{string.Join(" ", b)}]");
				}

				if (diff.Count == 0)
					exact++;
				else
					mismatches.Add($"quest {record.QuestId} line {record.QuestLineId} ({source}): {string.Join("; ", diff)}");
			}

			var distinct = mismatches.Select(m => m[..m.IndexOf('(')]).Distinct().ToList();
			Assert.True(withLists > 1000, $"only {withLists} records with reward data of {records} - the slice has narrowed");
			Assert.True(mismatches.Count == 0,
				$"{exact}/{withLists} records rebuild exactly; {mismatches.Count} do not, over {distinct.Count} distinct quest(s):\n"
				+ string.Join("\n", mismatches.GroupBy(m => m[..m.IndexOf('(')]).Select(g => $"  x{g.Count()} {g.First()}").Take(20)));
		}

		private static string Describe(QuestRewardItem e)
		{
			return $"{e.TemplateId}@{e.Level}x{e.Quantity}/{e.Key.Key1:x8}";
		}

		private static IEnumerable<(string Source, QuestcRecord Record)> Records()
		{
			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.FullPlayerCharacterIIR))
			{
				var declared = body.ReadU32Be(0);
				if (declared + 4 > body.Length)
					continue;

				FullPlayerCharacterIIR parts;
				try
				{
					var stream = new FaolanStream(body[4..]);
					stream.ReadUInt32();
					stream.ReadUInt32();
					stream.ReadUInt32();
					parts = SnapshotPackets.Parse(stream);
				}
				catch
				{
					continue;
				}

				foreach (var record in parts.SubClass.Quests?.Records ?? [])
					yield return (source, record);
			}

			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.QuestFullUpdateIIR))
			{
				QuestFullUpdateIIR full;
				try
				{
					full = QuestFullUpdateCodec.Parse(body.AsSpan(4));
				}
				catch
				{
					continue;
				}

				yield return (source, full.Record);
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Modules
{
	// Item identities + TEA keys from the quest reward lists inside every captured `Quest_c` journal record - a
	// fourth key source beside the inventory snapshot, `DispatchAddItem` and the `itemref://` chat links.
	//
	// Its breadth is of a different kind again. Every other source reads an item the capturing character held,
	// browsed or was shown; a quest's reward list describes what the quest will pay, so it reaches items nobody in
	// the corpus ever received. Most of the templates it carries are not in `Items` at all, and the key is the whole
	// payoff: `RdbItemImporter` can only decrypt a template we hold a key for, however present its RDB record is -
	// the quads are random per item and server-held, so there is no client-side route to them.
	//
	// The lists ride the login snapshot, which `items` already harvests - and it does not see these, because
	// `ItemHashHarvester` scans for the shapes an inventory entry takes. This reads them through the CODEC instead:
	// `QuestcRecord.Obj1..Obj4`, whose element is `QuestRewardItem`.
	//
	// Everything goes through `CaptureItemImporter.UpsertAsync`, whose rules are all never-downgrade, because a
	// reward entry carries no name and no HASH - only the identity quad, the pair and the level. Registered after
	// `items` so a held item's richer row wins the insert.
	public sealed class QuestRewardItemModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => ["items"];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Item identities + TEA keys from the quest REWARD lists in captured journal records (#893).";
		public string Name => "quest-reward-items";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var harvested = new Dictionary<uint, ItemHashHarvester.CapturedItem>();
			var elements = 0;
			var snapshots = 0;

			foreach (var capture in ctx.Captures)
			foreach (var packet in capture.Packets)
			{
				if (packet is not { IsIirCarrier: true, IsDownload: true, SubOpcode: FullPlayerCharacterCodec.Opcode })
					continue;

				FullPlayerCharacterIIR parts;
				try
				{
					parts = FullPlayerCharacterCodec.Parse(packet.Record);
				}
				catch
				{
					continue; // a snapshot shape the codec cannot split - `--report coverage` is where that is counted
				}

				snapshots++;
				foreach (var record in parts.SubClass.Quests?.Records ?? [])
				foreach (var reward in new[] { record.Obj1, record.Obj2, record.Obj3, record.Obj4 })
				foreach (var item in reward)
				{
					elements++;
					Record(harvested, item);
				}
			}

			if (harvested.Count == 0)
				return new ImportResult($"{snapshots} snapshot(s) carried no quest reward list.");

			var (total, inserted) = await CaptureItemImporter.UpsertAsync(ctx.Db, harvested);
			return new ImportResult($"{elements} reward element(s) over {harvested.Count} distinct template(s) in {snapshots} snapshot(s); {total} upserted, {inserted} new.");
		}

		// One reward folded into the harvest set. A template's key is per-template and stable, so the same template
		// in two quests is the same record and the first sighting wins.
		//
		// The `template2` half is carried only when it DIFFERS. It is identical to the first on all 7,347 captured
		// elements - the same "equal in the common case" the inventory entries show - and recording an equal
		// pair would write a `TemplateId2` that says nothing while looking like a measured second endpoint.
		private static void Record(IDictionary<uint, ItemHashHarvester.CapturedItem> into, QuestRewardItem reward)
		{
			var pair = reward.TemplateId2 != reward.TemplateId
				? new ItemHashHarvester.CapturedPair(reward.TemplateId2, reward.Key2.Key1, reward.Key2.Key2, reward.Key2.Key3, reward.Key2.Key4)
				: (ItemHashHarvester.CapturedPair?)null;

			// Hash 0: a reward element carries no hash word. The send path mints a token at send time, and the 0 is
			// the provenance record saying this identity came from a reward list.
			var captured = new ItemHashHarvester.CapturedItem(reward.Key.Key1, reward.Key.Key2, reward.Key.Key3, reward.Key.Key4, 0, reward.Level, pair);

			if (!into.TryGetValue(reward.TemplateId, out var existing))
			{
				into[reward.TemplateId] = captured;
				return;
			}

			if (existing.Level == 0 && captured.Level != 0)
				existing = existing with { Level = captured.Level };

			if (existing.Pair == null && captured.Pair != null)
				existing = existing with { Pair = captured.Pair };

			into[reward.TemplateId] = existing;
		}
	}
}

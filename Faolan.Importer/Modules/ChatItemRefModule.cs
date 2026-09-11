using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Modules
{
	// Item identities harvested from `itemref://` chat links - a third key source beside the inventory snapshot and
	// DispatchAddItem, and one of a different kind.
	//
	// There is no client-side source for an item's TEA key: the quads are random per item and held by Funcom's
	// server, so full-catalog decryption is impossible. What a chat link adds is breadth. Every other harvester
	// reads items the capturing character held or browsed; a
	// trade-channel link describes whatever a stranger was selling, so it reaches items nobody in the corpus ever
	// owned. And the key is the whole payoff - `RdbItemImporter` can only decrypt a template we hold a key for,
	// however present the RDB record is, so a link is not an id+name stub but the thing that unlocks the stats,
	// quality, value, damage and category behind it.
	//
	// Read straight out of the *.tshark.json, like `npc-chatter` and for the same reason: the shared reader keeps
	// only the Conan stream, and most of these ride the CHAT server (ChannelChat 0x0041 - the trade channel).
	// Carriers seen in the corpus are ChatServer 0x0041, ChatServer 0x0022 and the c2s Conan 0x2002, so this scans
	// every body for the scheme rather than allow-listing three opcodes: `itemref` is the only item-bearing href in
	// the whole corpus, and a fourth carrier should be harvested, not missed.
	//
	// A link is player-TYPED text. The client composes it, but the importer must not treat it as authoritative
	// over a snapshot-sourced row - so this goes through `CaptureItemImporter.UpsertAsync`, whose every rule is
	// never-downgrade: a name only fills an empty one, the hash is left alone (a link carries none), and a level
	// only fills a 0.
	public sealed class ChatItemRefModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => ["items"];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Item identities + TEA keys from itemref:// chat links (#751).";
		public string Name => "chat-itemref";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var harvested = new Dictionary<uint, ItemHashHarvester.CapturedItem>();
			var names = new Dictionary<uint, string>();
			var links = 0;
			var rejected = 0;

			foreach (var file in Directory.EnumerateFiles(ctx.CapturesDir, "*.tshark.json"))
			{
				foreach (var body in Bodies(file))
				{
					// Latin1 because that is the wire's string encoding. The FINDING is `ChatItemLinks`' job, in the
					// codec layer - locating a wire element is decode, and an importer that scanned for the anchor
					// itself would be the second place that knows the shape.
					var scan = ChatItemLinks.In(Encoding.Latin1.GetString(body));
					rejected += scan.Rejected;
					foreach (var link in scan.Links)
					{
						links++;
						names.TryAdd(link.TemplateId, link.Name);
						Record(harvested, link);
					}
				}
			}

			var (total, inserted) = await CaptureItemImporter.UpsertAsync(ctx.Db, harvested, names: names);
			return new ImportResult($"{links} itemref chat link(s) over {harvested.Count} distinct template(s); {total} upserted, {inserted} new" +
			                        (rejected > 0
				                        ? $"; {rejected} unreadable anchor(s) SKIPPED."
				                        : "."));
		}

		// One link folded into the harvest set. First sighting wins on everything the quad states - a template's key
		// is per-template and stable, so two links to the same item are the same record - and the pair fills on the
		// same never-downgrade rule the packet sources use, because only 1 of the corpus's 26 links carries one.
		private static void Record(IDictionary<uint, ItemHashHarvester.CapturedItem> into, ItemRefLink link)
		{
			// Key1, never key3. `key3` is sometimes the NullIdentity sentinel (both corpus sightings are
			// pre-existence items), and key2 belongs to the pair's second template.
			var pair = link.HasPair
				? new ItemHashHarvester.CapturedPair(link.TemplateId2, link.Key2.Key1, link.Key2.Key2, link.Key2.Key3, link.Key2.Key4)
				: (ItemHashHarvester.CapturedPair?)null;

			// Hash 0: the URL form drops the hash word the loot-chat body carries. The send path mints a token at send
			// time, and a 0 here is the provenance record saying this identity came from a link.
			var captured = new ItemHashHarvester.CapturedItem(link.Key1.Key1, link.Key1.Key2, link.Key1.Key3, link.Key1.Key4, 0, link.Level, pair);

			if (!into.TryGetValue(link.TemplateId, out var existing))
			{
				into[link.TemplateId] = captured;
				return;
			}

			if (existing.Level == 0 && captured.Level != 0)
				existing = existing with { Level = captured.Level };

			if (existing.Pair == null && captured.Pair != null)
				existing = existing with { Pair = captured.Pair };

			into[link.TemplateId] = existing;
		}

		private static IEnumerable<byte[]> Bodies(string file)
		{
			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(File.ReadAllText(file));
			}
			catch (JsonException)
			{
				yield break;
			}

			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
					yield break;

				foreach (var p in packets.EnumerateArray())
				{
					if (!p.TryGetProperty("BodyHex", out var bh) || bh.ValueKind != JsonValueKind.String)
						continue;

					var s = bh.GetString();
					if (string.IsNullOrEmpty(s) || (s.Length & 1) != 0)
						continue;

					byte[] body;
					try
					{
						body = Convert.FromHexString(s);
					}
					catch (FormatException)
					{
						continue;
					}

					yield return body;
				}
			}
		}
	}
}

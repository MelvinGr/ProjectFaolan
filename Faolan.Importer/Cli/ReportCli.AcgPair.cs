using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// `--report acg-pair` (#752): what the SECOND template on a `ProtoAcgItem` is.
	//
	// Every item on the wire carries `template1`/`template2`/`template3` and three decryption keys. We used to emit
	// `template2 = template1`, which is right for 8,445 of the 8,479 captured entries and was a guess for the rest.
	// This report settled it: the pair is a LEVEL RANGE - both halves are the same item, differing only in their
	// level fields, and the entry's own `level` says where between them this copy sits, which is what
	// `AcgItemData_t::GetInterpolatedItem` is named for. `Item.TemplateId2` + `Item.PairKey` now carry it.
	//
	// ⚠️ It was written up as the upstream cause of #754's socket-count anomaly and #756's `Bindings of Dark Terror`
	// Flags disagreement. That was WRONG and is recorded here so it is not re-derived: all of those items have
	// `template2 == template1`, so no generation is involved in either. #754's is legacy gem overflow (the client
	// has a `GemOverflow` token for exactly that state); #756's is its own question.
	//
	// The report walks the DUMPS for every ProtoAcgItem it can reach, tabulates how template2 relates to template1
	// and how the keys relate, and - the point - DECRYPTS BOTH HALVES of every differing pair (template1 with key1,
	// template2 with key2) and diffs their property tables field by field. `Items` only stores ONE key, so the pair's
	// second key exists nowhere but the captures; that is why this reads the wire rather than the catalog.
	public static partial class ReportCli
	{
		private static void AcgPair(IReadOnlyList<NamedCapture> captures, string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			var seen = new Dictionary<(uint T1, uint T2), Sighting>();
			var total = 0;

			// ⚠️ The key relations are tallied HERE, per sighting, and deliberately not off `seen.Values` afterwards.
			// `seen` keeps only the FIRST ProtoAcgItem of each (template1, template2) pair, so a count taken from it
			// is over ~340 representatives, not the ~13k sightings - and it would be blind to exactly the case that
			// matters: a later sighting of an already-seen pair whose key3 stopped equalling key1. The keys ought to
			// be per-TEMPLATE (keyN decrypts RDB template N), but that is the assumption under test, not a premise.
			var key3IsKey1 = 0;
			var key2IsKey1 = 0;
			var template3Set = 0;

			foreach (var capture in captures)
			foreach (var packet in capture.Packets)
			foreach (var acg in AcgItemsIn(packet))
			{
				if (acg.Template1 == 0)
					continue;

				total++;
				if (Same(acg.Key3, acg.Key1))
					key3IsKey1++;

				if (Same(acg.Key2, acg.Key1))
					key2IsKey1++;

				if (acg.Template3 != 0)
					template3Set++;

				var key = (acg.Template1, acg.Template2);
				if (!seen.TryGetValue(key, out var sighting))
					seen[key] = sighting = new Sighting(acg, capture.Name);

				sighting.Count++;
			}

			var differing = seen.Where(p => p.Key.T1 != p.Key.T2).ToList();
			Console.WriteLine($"acg-pair: {total} ProtoAcgItem sightings, {seen.Count} distinct (template1, template2) pairs.");
			Console.WriteLine($"  template2 == template1 : {seen.Count - differing.Count} pairs");
			Console.WriteLine($"  template2 != template1 : {differing.Count} pairs");
			Console.WriteLine($"    of which template2 == template1 + 1: {differing.Count(p => p.Key.T2 == p.Key.T1 + 1)}");
			Console.WriteLine();

			Console.WriteLine("  how the three keys relate, over every SIGHTING (not per distinct pair):");
			Console.WriteLine($"    key3 == key1  : {key3IsKey1} of {total}");
			Console.WriteLine($"    key2 == key1  : {key2IsKey1} of {total}");
			Console.WriteLine($"    template3 != 0: {template3Set} of {total}");
			Console.WriteLine();

			if (differing.Count == 0)
			{
				Console.WriteLine("  no differing pair in this corpus - nothing to diff.");
				return;
			}

			Console.WriteLine("  ---- the differing pairs, both halves decrypted and diffed ----");
			foreach (var (pair, sighting) in differing.OrderBy(p => p.Key.T1))
			{
				Console.WriteLine();
				Console.WriteLine($"  {pair.T1} -> {pair.T2}  (x{sighting.Count}, first in {sighting.Capture}; level {sighting.Item.Level}, hash 0x{sighting.Item.Hash:x8})");

				var first = ReadTemplate(rdb, pair.T1, sighting.Item.Key1);
				var second = ReadTemplate(rdb, pair.T2, sighting.Item.Key2);
				if (first == null || second == null)
				{
					Console.WriteLine($"    template1 {(first == null ? "UNREADABLE" : "ok")}, template2 {(second == null ? "UNREADABLE" : "ok")} - cannot diff");
					continue;
				}

				Console.WriteLine($"    sizes: template1 {first.Value.Body.Length} B, template2 {second.Value.Body.Length} B");
				Console.WriteLine($"    names: \"{first.Value.Table.Strings.GetValueOrDefault(21u, "")}\" / \"{second.Value.Table.Strings.GetValueOrDefault(21u, "")}\"");

				var fields = first.Value.Table.Fields.Keys.Union(second.Value.Table.Fields.Keys).OrderByDescending(f => f);
				foreach (var field in fields)
				{
					var a = first.Value.Table.Fields.TryGetValue(field, out var av) ? av.ToString() : "-";
					var b = second.Value.Table.Fields.TryGetValue(field, out var bv) ? bv.ToString() : "-";
					if (a != b)
						Console.WriteLine($"      field {field,4}: {a,12} -> {b,12}   <-- DIFFERS");
				}
			}
		}

		// Every ProtoAcgItem this packet can yield, from the sources that carry one: the login snapshot's containers
		// (including each entry's socketed gems, which are ProtoAcgItems in their own right) and the live add.
		private static IEnumerable<ProtoAcgItem> AcgItemsIn(CapturedPacket packet)
		{
			if (packet.WireOpcode == CapturedPacket.Wire(GameCharInterfaceMethods.DispatchAddItem))
			{
				AddItem? add = null;
				try
				{
					add = AddItemCodec.Parse(packet.Body);
				}
				catch (CodecParseException)
				{
					// another method sharing this index on a different interface
				}

				if (add?.Entry?.ItemData != null)
					yield return add.Entry.ItemData;

				yield break;
			}

			if (!packet.IsIirCarrier)
				yield break;

			ProtoFullCharacterIIR? root = null;
			try
			{
				var record = packet.Record;
				if (record.Length >= 4 && record.ReadU32Be(0) == FullPlayerCharacterCodec.Opcode)
					root = SnapshotRoot.Of(record);
			}
			catch
			{
				// not a snapshot - an ordinary outcome when sweeping a mixed capture
			}

			if (root == null)
				yield break;

			foreach (var container in root.Inventories)
			foreach (var entry in SnapshotRoot.EntriesOf(container))
			{
				if (entry.Item?.ItemData != null)
					yield return entry.Item.ItemData;

				foreach (var (_, gem) in SnapshotRoot.SocketsOf(entry))
					yield return gem;
			}
		}

		private static (byte[] Body, ItemTemplateProperties.Table Table)? ReadTemplate(RdbReader rdb, uint id, ProtoDecryptionKey? key)
		{
			if (key == null)
				return null;

			foreach (var type in new uint[] { 1000020, 1000620 })
			{
				if (!rdb.TryGet(type, id, out var e) || e.Size == 0 || !rdb.CanRead(e))
					continue;

				try
				{
					var template = rdb.ReadDecrypted(e, [key.Key1, key.Key2, key.Key3, key.Key4]);
					if (template.Length <= 12)
						return null;

					var body = template[12..];
					return (body, ItemTemplateProperties.Parse(body));
				}
				catch
				{
					// the wrong key for this half - reported by the caller as UNREADABLE
				}
			}

			return null;
		}

		private static bool Same(ProtoDecryptionKey? a, ProtoDecryptionKey? b)
		{
			return a != null && b != null && a.Key1 == b.Key1 && a.Key2 == b.Key2 && a.Key3 == b.Key3 && a.Key4 == b.Key4;
		}

		private sealed class Sighting(ProtoAcgItem item, string capture)
		{
			public string Capture { get; } = capture;
			public int Count { get; set; }
			public ProtoAcgItem Item { get; } = item;
		}
	}
}

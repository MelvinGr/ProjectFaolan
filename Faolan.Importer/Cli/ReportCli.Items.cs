using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		private static void ItemTemplate(string rdbDir, string? arg, string? outDir = null)
		{
			if (!uint.TryParse(arg, out var itemId))
			{
				Console.WriteLine($"item-template needs a numeric item id in --report-arg, got '{arg}'");
				return;
			}

			using var db = OpenDb();
			var item = db.Items.FirstOrDefault(i => i.Id == itemId);
			if (item == null)
			{
				// ⚠️ "not in DB" is not the answer - it is the question. There are two completely different reasons
				// an item is missing, and only one of them can ever be fixed by importing harder (#824):
				//
				//   * the client does not ship the template either - the id is not an item at all, or belongs to
				//     content this client build does not carry;
				//   * the client DOES ship it and we hold no KEY. That is the common case and it is a dead end by
				//     construction: an item's TEA key is server-assigned, cryptographically random per item, and
				//     reaches us only by riding a `ProtoAcgItem` in some capture (#203, closed won't-fix). No
				//     derivation, no key table, and `Items.Key1..4` are NOT NULL because the CLIENT needs the quad to
				//     decrypt the template out of its own RDB - so a name-only row would be unusable anyway.
				//
				// Saying which costs one index lookup, so say it rather than making the next reader re-derive it.
				Console.WriteLine($"item {itemId} not in DB");
				using var probe = OpenRdb(rdbDir);
				var shipped = new uint[] { 1000020, 1000620 }.Where(t => probe.TryGet(t, itemId, out var e) && e.Size > 0).ToList();
				Console.WriteLine(shipped.Count == 0
					? "  ...and the client ships no 1000020/1000620 template for it either - this id is not an item template in this build."
					: $"  ...but the client DOES ship it (RDB type {string.Join("/", shipped)}). The blocker is the KEY, not the catalog:"
					  + " an item's TEA key only ever arrives on the wire, so this item needs a capture that references it (#203).");
				return;
			}

			Console.WriteLine($"item {itemId} \"{item.Name}\" type={item.Type} cat={item.SearchCategory} key={item.Key.Key1:X8},{item.Key.Key2:X8},{item.Key.Key3:X8},{item.Key.Key4:X8}");
			using var rdb = OpenRdb(rdbDir);
			var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
			byte[]? template = null;
			foreach (var type in new uint[] { 1000020, 1000620 })
			{
				if (rdb.TryGet(type, itemId, out var e) && e.Size > 0 && rdb.CanRead(e))
				{
					try
					{
						template = rdb.ReadDecrypted(e, key);
						Console.WriteLine($"decrypted type={type} {template.Length} B");
						break;
					}
					catch (Exception ex)
					{
						Console.WriteLine($"decrypt type={type} failed: {ex.Message}");
					}
				}
			}

			if (template == null)
			{
				Console.WriteLine("no template entry");
				return;
			}

			if (outDir != null)
			{
				Directory.CreateDirectory(outDir);
				File.WriteAllBytes(Path.Combine(outDir, $"item_{itemId}.bin"), template);
			}

			var body = template.AsSpan(12);

			// Every printable ASCII run >= 3 chars (asset/mesh names look like "armor_..._body" / "*bodymesh_NNN").
			Console.WriteLine("-- strings --");
			for (var i = 0; i < body.Length;)
			{
				var j = i;
				while (j < body.Length && body[j] >= 32 && body[j] <= 126)
					j++;
				if (j - i >= 3)
					Console.WriteLine($"  @{i,5} {Encoding.ASCII.GetString(body.Slice(i, j - i))}");
				i = j < body.Length
					? j + 1
					: j;
			}

			// Property table: the longest run of [fieldId][value] u32 pairs with strictly-decreasing fieldId in (0,2000].
			Console.WriteLine("-- property table (fieldId=value) --");
			var words = body.Length / 4;
			var bestStart = -1;
			var bestLen = 0;
			for (var start = 0; start + 1 < words; start++)
			{
				uint last = 0;
				var have = false;
				var len = 0;
				for (var j = start; j + 1 < words; j += 2)
				{
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (have && fid >= last))
						break;

					last = fid;
					have = true;
					len++;
				}

				if (len > bestLen)
				{
					bestLen = len;
					bestStart = start;
				}
			}

			if (bestStart >= 0)
			{
				uint last = 0;
				var have = false;
				for (var j = bestStart; j + 1 < words; j += 2)
				{
					var fid = body.ReadU32Le(j * 4);
					if (fid == 0 || fid > 2000 || (have && fid >= last))
						break;

					var val = body.ReadU32Le((j + 1) * 4);
					var resolves = "";
					foreach (var t in new uint[] { 1010030, 1010201, 1010001, 1010200, 1000700, 1010700 })
					{
						if (val > 1000 && rdb.TryGet(t, val, out var re) && re.Size > 0)
							resolves += $" [->{t} {(re.File == 0xff ? "CDN" : "loc")}]";
					}

					Console.WriteLine($"  field {fid,4} = {val}{resolves}");
					last = fid;
					have = true;
				}
			}

			// Every u32 that resolves to a known character-asset RDB entry - candidate worn-mesh references.
			Console.WriteLine("-- u32 -> RDB asset refs --");
			var refTypes = new uint[] { 1010030, 1010201, 1010001, 1010200 };
			for (var i = 0; i + 4 <= body.Length; i += 4)
			{
				var v = body.ReadU32Le(i);
				if (v < 1000)
					continue;

				foreach (var t in refTypes)
				{
					if (rdb.TryGet(t, v, out var re) && re.Size > 0)
						Console.WriteLine($"  @{i,5} {v} -> type {t} ({(re.File == 0xff ? "CDN" : "local")} {re.Size} B)");
				}
			}
		}

		private static void Equip(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					if (body == null || !SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					{
						Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} | (unreadable)");
						continue;
					}

					var slots = read
						.Message.InventoryEntries.Select(entry => NpcSpawnEquipmentBuilder.TryLift<NpcSpawnEquipment>(entry, out var row)
							// The row stores only slot/template/level now (the quad + hash are the Items row Template FKs to);
							// read the identity for the report off the captured entry itself.
							? $"slot{row.Pos}=tpl{row.Template}[k={entry.Item.Key1.Key1},{entry.Item.Key1.Key2},{entry.Item.Key1.Key3},{entry.Item.Key1.Key4} lvl={row.Level} hash={entry.Item.Hash}]"
							: "slot?=unliftable")
						.ToList();
					Console.WriteLine($"{c.Name[..Math.Min(44, c.Name.Length)]} | handle 0x{e.Handle:X} pos=({e.X:0.#},{e.Z:0.#})");
					foreach (var s in slots)
						Console.WriteLine($"    {s}");
				}
		}
	}
}

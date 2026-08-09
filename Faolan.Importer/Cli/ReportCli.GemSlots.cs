using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report gem-slots` (#754): how many sockets an item HAS, and what a gem is, straight out of the item template.
	//
	// Socketing consumes the gem, so accepting the c2s action without knowing an item's socket count would let a
	// player destroy one against an item with nowhere to put it. The client says both exist - `ItemInfoView.cpp`
	// carries a `GemSlots` token and a `GemOverflow` line, and asserts on an "Invalid gem slot colour" - but nothing
	// we held said what the bound was.
	//
	// The client's own stat-name table (LDB 615) names the fields, and item template field ids ARE wire stat ids:
	//
	//     113  Gem Size        114  Gem Slots        115..118  Gem Slot 1..4
	//
	// Four per-slot fields, which matches the four `ItemSlot1..4` ACG stat pairs the socket list uses - so the
	// corpus' socket indices 0/1/2 really were a capture limit and the game's bound is four.
	//
	// ⚠️ **The cross-check REFUTES field 114 as the whole answer, and that is the report's most useful output.**
	// Its distribution is exactly a socket count's (612 templates with 1, 14 with 2, 12 with 3, 9 with 4, the
	// four-slot ones being high-end weapons) - but of the six templates whose sockets we have actually observed,
	// THREE carry three gems while declaring one slot (`Ornate Gold Ring`, `Ornate Gold Necklace`,
	// `Filigree Steelsilk Cloak`). The walk is not at fault: their property tables read cleanly, in a proper
	// descending run, with `114 = 1`.
	//
	// So template1's count is at best a BASE that a generated item can exceed - the same gap #756 hit from the other
	// side, where the client's bind-on-pickup test reads `AcgItemData_t::GetInterpolatedItem(quad)[+0xC8]` rather
	// than template1's own word. Field 113 points the same way: it is on 632 templates including plain weapons
	// (`Bloodsilver Awl Pike` "size" 3227), with values clustered at ~793 intervals, which is not a gem size.
	// Decoding the ACG pair (#752) is upstream of both.
	//
	// It stays a report rather than a column because of that: measuring is right, believing it is not.
	public static partial class ReportCli
	{
		private const uint GemSizeField = 113;
		private const uint GemSlotsField = 114;
		private static readonly uint[] GemSlotFields = [115, 116, 117, 118];

		private static void GemSlots(string rdbDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);

			var items = db.Items
				.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id)
				.ToList();
			var names = items.ToDictionary(i => i.Id, i => i.Name ?? "");

			var slots = new Dictionary<uint, uint>(); // template -> declared socket count
			var sizes = new Dictionary<uint, uint>(); // template -> gem size
			var perSlot = new Dictionary<uint, uint[]>();
			var decrypted = 0;

			foreach (var item in items)
			{
				var body = TryDecryptTemplate(rdb, item);
				if (body == null)
					continue;

				decrypted++;
				var table = ItemTemplateProperties.Parse(body);
				if (table.Fields.TryGetValue(GemSlotsField, out var count) && count > 0)
				{
					slots[item.Id] = count;
					perSlot[item.Id] = GemSlotFields.Select(table.Value).ToArray();
				}

				if (table.Fields.TryGetValue(GemSizeField, out var size) && size > 0)
					sizes[item.Id] = size;
			}

			Console.WriteLine($"gem-slots: {decrypted} templates read.");
			Console.WriteLine($"  {slots.Count} declare Gem Slots (114) > 0 - these are the SOCKETABLE items");
			Console.WriteLine($"  {sizes.Count} declare Gem Size (113) > 0 - these are the GEMS");
			Console.WriteLine($"  {slots.Keys.Intersect(sizes.Keys).Count()} declare both (expected 0 if the two are disjoint roles)");
			Console.WriteLine();

			Console.WriteLine("  socket-count distribution:");
			foreach (var (count, n) in slots.Values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())))
				Console.WriteLine($"    {count} socket(s): {n} template(s)");

			Console.WriteLine();
			Console.WriteLine("  gem-size distribution:");
			foreach (var (size, n) in sizes.Values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())))
				Console.WriteLine($"    size {size}: {n} gem(s)");

			// THE CROSS-CHECK. Every socket we have actually observed, against the count the item's own template
			// declares. An item carrying more gems than it declares slots means field 114 is not the socket count.
			Console.WriteLine();
			var observed = db.ItemInstanceSockets
				.Include(s => s.ItemInstance)
				.AsEnumerable()
				.GroupBy(s => s.ItemInstance.TemplateId)
				.ToDictionary(g => g.Key, g => g.Max(s => s.SocketIndex) + 1);

			Console.WriteLine($"  cross-check against {observed.Count} template(s) with sockets actually observed:");
			int ok = 0, over = 0, undeclared = 0;
			foreach (var (template, used) in observed.OrderBy(p => p.Key))
			{
				var declared = slots.GetValueOrDefault(template);
				var verdict = declared == 0
					? "NO Gem Slots field"
					: used <= declared
						? "ok"
						: "*** MORE GEMS THAN SLOTS ***";
				if (declared == 0)
					undeclared++;
				else if (used <= declared)
					ok++;
				else
					over++;

				Console.WriteLine($"    {template,-9} \"{names.GetValueOrDefault(template, "")}\"  observed {used}, declares {declared}  {verdict}");
			}

			Console.WriteLine($"    -> {ok} consistent, {over} over-filled, {undeclared} with no field");

			Console.WriteLine();
			Console.WriteLine("  a sample of socketable items, with their per-slot fields (115..118):");
			foreach (var (id, count) in slots.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(15))
				Console.WriteLine($"    {id,-9} \"{names.GetValueOrDefault(id, "")}\"  slots={count}  perSlot=[{string.Join(",", perSlot[id])}]");

			Console.WriteLine();
			Console.WriteLine("  a sample of gems:");
			foreach (var (id, size) in sizes.OrderBy(p => p.Key).Take(15))
				Console.WriteLine($"    {id,-9} \"{names.GetValueOrDefault(id, "")}\"  size={size}");
		}
	}
}

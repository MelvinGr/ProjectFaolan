using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Protobuf;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
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
	// The client's OWN stat-name table (`docs/re/aoc-client-stat-names.tsv`, recovered from the exe - richer than
	// LDB 615, which only names a subset) gives the whole gem model, and item template field ids ARE stat ids:
	//
	//     113  GemSize          114  NumGemSlots      115..118  GemSlot1..4
	//     694..697  GemSlot1Color..GemSlot4Color      698  GemColor    699  GemItemType    916  GemTier
	//
	// So an item declares HOW MANY sockets it has and what COLOUR each is, and a gem declares its own colour, size
	// and tier - which is what the client's `ItemInfoView::UpdateFromItem: Invalid gem slot colour` assert checks.
	// Four per-slot fields on both sides, matching the four `ItemSlot1..4` ACG stat pairs the socket list uses, so
	// the corpus' socket indices 0/1/2 were a capture limit and the game's bound is four.
	//
	// These are not coincidental id matches: `Ornate Gold Necklace`'s property run carries `916=9` (GemTier),
	// `694=6` (GemSlot1Color) and `114=1` (NumGemSlots) together, i.e. the whole block. The client reads 114 itself -
	// the tooltip's GemSlots line is gated on `getProp(0x72) > 0`.
	//
	// ## ⚠️ An item CAN carry more gems than it declares slots, and that is not a decode failure
	//
	// Of the five templates the dumps show sockets on, three carry three gems while declaring one slot
	// (`Ornate Gold Ring`, `Ornate Gold Necklace`, `Filigree Steelsilk Cloak`). That reads as a refutation and is not
	// one: Melvin's own characters are retail characters, and he identified these as items left over from an older
	// version of the game whose slot counts were later reduced.
	//
	// The client agrees, because it has a WORD for it. `ItemInfoView.cpp` carries a `GemOverflow` token, a
	// `GemOverflowInfo` explainer, and a clickable `<BR> <a href="gemoverflow">%s</a>` link whose handler
	// (`FUN_012fdf30`) renders both. A game does not build a UI affordance for "this item has more gems than slots"
	// unless that state legitimately exists.
	//
	// So for #754 the rule is: a NEW socket needs `index < NumGemSlots` and a matching colour; an item already over
	// its count is a legacy state to render and never add to. The counts are measured from the DUMPS rather than
	// from ItemInstanceSockets, because the DB's sockets are attached by CaptureSocketImporter's cell match - which
	// would make the cross-check measure our own importer.
	//
	// It stays a report rather than columns until #754 actually needs them.
	public static partial class ReportCli
	{
		private const uint GemSizeField = 113;
		private const uint GemSlotsField = 114; // NumGemSlots
		private const uint GemColorField = 698;
		private const uint GemTierField = 916;
		private static readonly uint[] GemSlotColorFields = [694, 695, 696, 697];
		private static readonly uint[] GemSlotFields = [115, 116, 117, 118];

		// Every login-snapshot container entry in this packet, so the caller can ask how many sockets it CARRIED.
		private static IEnumerable<ProtoInventoryGame.InventoryListEntry> SocketedEntriesIn(CapturedPacket packet)
		{
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
				// not a snapshot
			}

			if (root == null)
				yield break;

			foreach (var container in root.Inventories)
			foreach (var entry in SnapshotRoot.EntriesOf(container))
				yield return entry;
		}

		private static void GemSlots(string rdbDir, IReadOnlyList<NamedCapture> captures)
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
			var colours = new Dictionary<uint, uint>(); // gem -> GemColor (698)
			var tiers = new Dictionary<uint, uint>(); // gem -> GemTier (916)
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
					perSlot[item.Id] = GemSlotFields.Concat(GemSlotColorFields).Select(table.Value).ToArray();
				}

				if (table.Fields.TryGetValue(GemSizeField, out var size) && size > 0)
				{
					sizes[item.Id] = size;
					colours[item.Id] = table.Value(GemColorField);
					tiers[item.Id] = table.Value(GemTierField);
				}
			}

			Console.WriteLine($"gem-slots: {decrypted} templates read.");
			Console.WriteLine($"  {slots.Count} declare NumGemSlots (114) > 0 - these are the SOCKETABLE items");
			Console.WriteLine($"  {sizes.Count} declare GemSize (113) > 0");
			// ⚠️ 113 and 114 are NOT disjoint item/gem roles - most socketable items carry both, and plain weapons carry
			// 113 too (`Bloodsilver Awl Pike` "size" 3227). So GemSize is not a "this is a gem" marker; only
			// NumGemSlots > 0 reliably says "this can be socketed". Whatever 113 means on a non-gem is undecoded.
			Console.WriteLine($"  {slots.Keys.Intersect(sizes.Keys).Count()} declare BOTH - so 113 is not a gem marker");
			Console.WriteLine();

			Console.WriteLine("  socket-count distribution (the per-slot COLOURS below corroborate it: a 4-slot weapon");
			Console.WriteLine("  declares four distinct colours [12,13,14,15], a 3-slot catalyst three and a zero [7,8,9,0]):");
			foreach (var (count, n) in slots.Values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())))
				Console.WriteLine($"    {count} socket(s): {n} template(s)");

			Console.WriteLine();
			Console.WriteLine("  gem-size distribution:");
			foreach (var (size, n) in sizes.Values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())))
				Console.WriteLine($"    size {size}: {n} gem(s)");

			// THE CROSS-CHECK. Every socket we have actually observed, against the count the item's own template
			// declares. An item carrying more gems than it declares slots means field 114 is not the socket count.
			//
			// ⚠️ Measured from the DUMPS, not from ItemInstanceSockets. The DB's sockets are attached by
			// CaptureSocketImporter, which matches a captured entry onto an existing character's cell and reports
			// "4/6 whose cell still matches" - so a moved cell could attach a gem list to the WRONG item, and the
			// cross-check would then be measuring our own importer rather than retail. The capture's own entry says
			// which item carried which gems with no matching step in between.
			Console.WriteLine();
			var observed = new Dictionary<uint, uint>();
			foreach (var capture in captures)
			foreach (var packet in capture.Packets)
			foreach (var entry in SocketedEntriesIn(packet))
			{
				var template = entry.Item?.ItemData?.Template1 ?? 0;
				if (template == 0)
					continue;

				var sockets = SnapshotRoot.SocketsOf(entry).Select(x => x.Socket + 1).ToArray();
				var used = sockets.Length == 0
					? 0u
					: sockets.Max();
				if (used > observed.GetValueOrDefault(template))
					observed[template] = used;
			}

			Console.WriteLine($"  cross-check against {observed.Count} template(s) with sockets actually observed:");
			int ok = 0, over = 0, undeclared = 0;
			foreach (var (template, used) in observed.OrderBy(p => p.Key))
			{
				var declared = slots.GetValueOrDefault(template);
				var verdict = declared == 0
					? "NO Gem Slots field"
					: used <= declared
						? "ok"
						: "OVERFLOW (legacy - more gems than slots; the client's own GemOverflow state)";
				if (declared == 0)
					undeclared++;
				else if (used <= declared)
					ok++;
				else
					over++;

				Console.WriteLine($"    {template,-9} \"{names.GetValueOrDefault(template, "")}\"  observed {used}, declares {declared}  {verdict}");
			}

			Console.WriteLine($"    -> {ok} within their declared count, {over} in legacy OVERFLOW, {undeclared} with no NumGemSlots field");

			Console.WriteLine();
			Console.WriteLine("  a sample of socketable items - perSlot is [GemSlot1..4, GemSlot1Color..4Color]:");
			foreach (var (id, count) in slots.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(15))
				Console.WriteLine($"    {id,-9} \"{names.GetValueOrDefault(id, "")}\"  slots={count}  perSlot=[{string.Join(",", perSlot[id])}]");

			Console.WriteLine();
			Console.WriteLine("  a sample of gems (size / colour / tier):");
			foreach (var (id, size) in sizes.OrderBy(p => p.Key).Take(15))
				Console.WriteLine($"    {id,-9} \"{names.GetValueOrDefault(id, "")}\"  size={size} colour={colours.GetValueOrDefault(id)} tier={tiers.GetValueOrDefault(id)}");
		}
	}
}

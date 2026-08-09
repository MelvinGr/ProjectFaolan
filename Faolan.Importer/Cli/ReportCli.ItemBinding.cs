using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report item-binding` (#756): what makes an item BIND, measured on both sides at once - the client's own rule,
	// read out of its item-tooltip builder, evaluated over every RDB template we hold a key for; and the wire's
	// `Flags` word (stat 0), taken from the DUMPS at the two moments it can change - an item ARRIVING
	// (DispatchAddItem) and a later DispatchSetItemStat on stat 0.
	//
	// ## Where the rule comes from
	//
	// The client names the three tooltip lines itself. They are LDB category-100 tokens, and the token NAMES are in
	// the exe's `InventoryGUI\ItemInfoView.cpp` string block, consecutive with their neighbours:
	//
	//     CurrentlyEquipped · DropsInPvP · BoundToCharacter · BindsOnPickup · BindsOnEquip · NotCancelable ·
	//     CanNotBeUnbound · GemSlots
	//
	//     elf28("BoundToCharacter") = 202900962 -> LDB[100] "Character Bound"
	//     elf28("BindsOnPickup")    = 174799584 -> LDB[100] "Binds when Picked Up"
	//     elf28("BindsOnEquip")     =  45924736 -> LDB[100] "Binds when Equipped"
	//
	// `ItemInfoView::UpdateFromItem` (FUN_014d5980) picks between them with two predicates over the item:
	//
	//     binds on EQUIP  (FUN_014d51a0)  getProp(177) & 0x20                     "More Flags" bit 5
	//                                  || getProp(499) > 1 && getProp(30) & 0x04  "Power Level" > 1 AND "Can" bit 2
	//     binds on PICKUP (FUN_014d5210)  AcgItemData_t::GetInterpolatedItem(quad)[+0xC8] >> 26 & 1
	//     is bound NOW                    the ITEM's own flag test, vt+0xA0 with 0x04000000 - i.e. wire stat 0 bit 26
	//
	// `[+0xC8]` is the interpolated item's Flags word: `InventoryGame_c::AddTemplateItem` (FUN_0076e9f0) reads exactly
	// that dword and writes it into the new item's **stat 0**, so the template's `Flags` property IS the item's
	// starting Flags. That is the link this report measures.
	//
	// ## What it prints
	//
	//  1. the pinned templates, with every property field the rule reads and both predicates evaluated;
	//  2. an RDB census - how many templates carry each bit of `Flags`, and how many bind on equip by each clause;
	//  3. the wire side from the dumps - the arrival Flags of every DispatchAddItem against its template's `Flags`,
	//     and every stat-0 DispatchSetItemStat, which is a binding TRANSITION and the only direct sighting of one.
	public static partial class ReportCli
	{
		// The wire `Flags` bit the client tests for "this item is bound to me" (vt+0xA0 with 0x04000000).
		private const uint BoundFlagBit = 1u << 26;

		// The property fields the client's two binding predicates read. Named by the client's own stat-name table
		// (LDB 615): field ids in an item template ARE wire stat ids.
		private const uint FlagsField = 0; // "Flags"       - the starting stat-0 word; bit 26 is the bound flag
		private const uint CanField = 30; // "Can"          - bit 2 participates in the bind-on-equip rule
		private const uint MoreFlagsField = 177; // "More Flags" - bit 5 alone makes an item bind on equip
		private const uint PowerLevelField = 499; // "Power Level" - the quality tier; > 1 gates the Can-bit clause

		// The four templates the decode is pinned on, three of them with an independent in-client or on-wire
		// observation of what they actually do. Printed in full so a re-run says whether the rule still reproduces
		// them - this is the report's own regression check.
		private static readonly (uint Template, string Observed)[] PinnedTemplates =
		[
			(3908313, "GM tooltip: 'Character Bound : Binds when Picked Up' (Cloak of Hugin)"),
			(3762969, "GM tooltip: 'Binds when Equipped' (Bindings of Dark Terror)"),
			(3721359, "ON THE WIRE: arrived unbound (stat 0 = 1), bound on equip (stat 0 = 0x04000001) - Imbued Frayed Circlet"),
			(3361557, "control - starter Slave Manacles, never bound in any capture"),
			(180450, "control - Daggamalt, a drink")
		];

		private static void ItemBinding(IReadOnlyList<NamedCapture> captures, string rdbDir)
		{
			using var db = OpenDb();
			using var rdb = OpenRdb(rdbDir);

			var items = db.Items
				.Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id)
				.ToList();

			var properties = new Dictionary<uint, ItemTemplateProperties.Table>();
			var names = items.ToDictionary(i => i.Id, i => i.Name ?? "");
			int decrypted = 0, unreadable = 0, withFlags = 0;
			foreach (var item in items)
			{
				var body = TryDecryptTemplate(rdb, item);
				if (body == null)
				{
					unreadable++;
					continue;
				}

				decrypted++;
				var table = ItemTemplateProperties.Parse(body);
				properties[item.Id] = table;
				if (table.Fields.ContainsKey(FlagsField))
					withFlags++;
			}

			Console.WriteLine($"item-binding: {items.Count} keyed items, {decrypted} templates decrypted ({unreadable} without a readable/decryptable entry).");
			Console.WriteLine($"  {withFlags} of {decrypted} carry property field 0 (Flags) - the walk reaching it at all is what says the property table was read whole.");
			Console.WriteLine();

			PrintPinned(properties, names);
			PrintFlagsCensus(properties);
			PrintEquipRuleCensus(properties, names);
			PrintWireFlags(captures, properties, names);
		}

		private static byte[]? TryDecryptTemplate(RdbReader rdb, Item item)
		{
			var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
			foreach (var type in new uint[] { 1000020, 1000620 })
			{
				if (!rdb.TryGet(type, item.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
					continue;

				try
				{
					var template = rdb.ReadDecrypted(e, key);
					return template.Length > 12
						? template[12..]
						: null;
				}
				catch
				{
					// a wrong/stale key - counted by the caller, not fatal to the sweep
				}
			}

			return null;
		}

		// The client's bind-on-equip predicate, transcribed from FUN_014d51a0. `getProp` on a field the template does
		// not carry returns 0 in the client, which is what Table.Value does, so an absent field reads the same here.
		private static bool BindsOnEquip(ItemTemplateProperties.Table t)
		{
			return (t.Value(MoreFlagsField) & 0x20) != 0 ||
				(t.Value(PowerLevelField) > 1 && (t.Value(CanField) & 0x04) != 0);
		}

		private static void PrintPinned(IReadOnlyDictionary<uint, ItemTemplateProperties.Table> properties, IReadOnlyDictionary<uint, string> names)
		{
			Console.WriteLine("-- the pinned templates (the rule evaluated against what was actually observed) --");
			foreach (var (template, observed) in PinnedTemplates)
			{
				if (!properties.TryGetValue(template, out var t))
				{
					Console.WriteLine($"  {template,-9} NOT IN THE CATALOG (no key, or no readable RDB entry)");
					continue;
				}

				var flags = t.Value(FlagsField);
				var name = t.Strings.GetValueOrDefault(21u, names.GetValueOrDefault(template, ""));
				Console.WriteLine($"  {template,-9} \"{name}\"");
				Console.WriteLine($"      observed: {observed}");
				Console.WriteLine($"      Flags(0)=0x{flags:x8} [{BitList(flags)}]   Can(30)={t.Value(CanField)}   MoreFlags(177)={t.Value(MoreFlagsField)}   PowerLevel(499)={t.Value(PowerLevelField)}");
				Console.WriteLine($"      -> binds on EQUIP  = {BindsOnEquip(t)}");
				Console.WriteLine($"      -> Flags bit 26    = {(flags & BoundFlagBit) != 0}   (what FUN_014d5210 reads for 'binds on pickup')");
			}

			Console.WriteLine();
		}

		private static void PrintFlagsCensus(IReadOnlyDictionary<uint, ItemTemplateProperties.Table> properties)
		{
			var perBit = new int[32];
			var distinct = new Dictionary<uint, int>();
			foreach (var t in properties.Values)
			{
				var flags = t.Value(FlagsField);
				distinct[flags] = distinct.GetValueOrDefault(flags) + 1;
				for (var bit = 0; bit < 32; bit++)
				{
					if ((flags & (1u << bit)) != 0)
						perBit[bit]++;
				}
			}

			Console.WriteLine($"-- property field 0 (Flags) across the catalog: {distinct.Count} distinct values --");
			foreach (var (value, n) in distinct.OrderByDescending(p => p.Value).Take(20))
				Console.WriteLine($"  0x{value:x8}  x{n,-6} [{BitList(value)}]");

			Console.WriteLine();
			Console.WriteLine("  per bit:");
			for (var bit = 0; bit < 32; bit++)
			{
				if (perBit[bit] > 0)
					Console.WriteLine($"    bit {bit,2} (0x{1u << bit:x8}): {perBit[bit]} template(s)");
			}

			Console.WriteLine();
		}

		private static void PrintEquipRuleCensus(IReadOnlyDictionary<uint, ItemTemplateProperties.Table> properties, IReadOnlyDictionary<uint, string> names)
		{
			int byMoreFlags = 0, byQualityAndCan = 0, both = 0, neither = 0;
			foreach (var t in properties.Values)
			{
				var a = (t.Value(MoreFlagsField) & 0x20) != 0;
				var b = t.Value(PowerLevelField) > 1 && (t.Value(CanField) & 0x04) != 0;
				if (a && b)
					both++;
				else if (a)
					byMoreFlags++;
				else if (b)
					byQualityAndCan++;
				else
					neither++;
			}

			Console.WriteLine("-- the client's bind-on-EQUIP rule over the catalog --");
			Console.WriteLine($"  MoreFlags(177) bit 5 only          : {byMoreFlags}");
			Console.WriteLine($"  PowerLevel(499)>1 AND Can(30) bit 2: {byQualityAndCan}");
			Console.WriteLine($"  both clauses                       : {both}");
			Console.WriteLine($"  binds on equip (total)             : {byMoreFlags + byQualityAndCan + both} of {properties.Count}");
			Console.WriteLine($"  does not bind on equip             : {neither}");

			// The Can bit on its own is nearly universal, so the clause is really carried by the QUALITY gate; saying
			// so keeps the next reader from reading "Can bit 2" as the binding flag.
			var canBit = properties.Values.Count(t => (t.Value(CanField) & 0x04) != 0);
			var quality = properties.Values.Count(t => t.Value(PowerLevelField) > 1);
			Console.WriteLine($"  (of which: {canBit} carry Can bit 2 at all, {quality} carry PowerLevel > 1 - whichever is the RARER is the one doing the work)");
			Console.WriteLine();

			Console.WriteLine("  a sample of the templates the rule says bind on equip:");
			foreach (var (id, t) in properties.Where(p => BindsOnEquip(p.Value)).Take(12))
				Console.WriteLine($"    {id,-9} \"{t.Strings.GetValueOrDefault(21u, names.GetValueOrDefault(id, ""))}\"  PowerLevel={t.Value(PowerLevelField)} MoreFlags={t.Value(MoreFlagsField)}");

			Console.WriteLine();
		}

		// The wire half, from the DUMPS (not the corpus slice - an absolute count has to come from the dumps).
		//
		// Two sightings matter and they are different questions. A DispatchAddItem is the moment an item is CREATED
		// in a container, so its Flags are the BIRTH state - which is what `AddTemplateItem` says should equal the
		// template's own Flags. A stat-0 DispatchSetItemStat is a later TRANSITION of that word on an existing cell,
		// which is the binding event itself; #756 had never seen one until the 2026-08-09 equip capture.
		private static void PrintWireFlags(IReadOnlyList<NamedCapture> captures, IReadOnlyDictionary<uint, ItemTemplateProperties.Table> properties,
			IReadOnlyDictionary<uint, string> names)
		{
			var adds = new List<(string Capture, uint Template, uint Flags)>();
			var transitions = new List<(string Capture, string Time, ContainerKind Container, uint Slot, uint Value)>();

			foreach (var capture in captures)
			foreach (var packet in capture.Packets)
			{
				var isAdd = packet.WireOpcode == CapturedPacket.Wire(GameCharInterfaceMethods.DispatchAddItem);
				var isStat = packet.WireOpcode == CapturedPacket.Wire(GameCharInterfaceMethods.DispatchSetItemStat);
				if (!isAdd && !isStat)
					continue;

				var body = packet.Body;
				if (isAdd)
				{
					AddItem add;
					try
					{
						add = AddItemCodec.Parse(body);
					}
					catch (CodecParseException)
					{
						continue; // another method sharing this index on a different interface
					}

					var template = add.Entry?.ItemData?.Template1 ?? 0;
					if (template == 0)
						continue;

					var flags = add.Entry!.StatLists.FirstOrDefault(s => s.Stat == FlagsField)?.Value;
					if (flags != null)
						adds.Add((capture.Name, template, flags.Value));
				}
				else
				{
					SetItemStat stat;
					try
					{
						stat = SetItemStatCodec.Parse(body);
					}
					catch (CodecParseException)
					{
						continue;
					}

					if (stat.StatId == FlagsField)
						transitions.Add((capture.Name, new DateTime(packet.Ticks).ToString("HH:mm:ss"), stat.Container, stat.Slot, stat.Value));
				}
			}

			Console.WriteLine($"-- the wire's Flags word: {adds.Count} DispatchAddItem arrivals, {transitions.Count} stat-0 DispatchSetItemStat transitions --");
			Console.WriteLine();

			int agree = 0, disagree = 0, noTemplate = 0;
			var mismatches = new List<string>();
			foreach (var (capture, template, flags) in adds)
			{
				if (!properties.TryGetValue(template, out var t))
				{
					noTemplate++;
					continue;
				}

				var templateFlags = t.Value(FlagsField);
				if (templateFlags == flags)
				{
					agree++;
					continue;
				}

				disagree++;
				if (mismatches.Count < 20)
				{
					mismatches.Add(
						$"    {template,-9} \"{t.Strings.GetValueOrDefault(21u, names.GetValueOrDefault(template, ""))}\"  wire=0x{flags:x8} template=0x{templateFlags:x8}  differ in [{BitList(flags ^ templateFlags)}]  ({capture})");
				}
			}

			Console.WriteLine("  arrival Flags vs the TEMPLATE's Flags field - does an item start life with its template's flags?");
			Console.WriteLine($"    agree {agree}   differ {disagree}   template unreadable {noTemplate}");
			foreach (var line in mismatches)
				Console.WriteLine(line);

			Console.WriteLine();
			Console.WriteLine("  every stat-0 transition (the binding EVENT):");
			foreach (var group in transitions.GroupBy(x => (x.Container, x.Value)).OrderByDescending(g => g.Count()))
			{
				var (container, value) = group.Key;
				Console.WriteLine($"    container {(uint)container,-4} {container,-9} -> 0x{value:x8} [{BitList(value)}]  x{group.Count()}");
			}

			foreach (var t in transitions.Take(20))
				Console.WriteLine($"      {t.Capture} {t.Time}  {t.Container} slot {t.Slot} = 0x{t.Value:x8}");

			Console.WriteLine();
		}

		private static string BitList(uint value)
		{
			var bits = Enumerable.Range(0, 32).Where(b => (value & (1u << b)) != 0);
			return string.Join(",", bits);
		}
	}
}

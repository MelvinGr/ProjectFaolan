using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report item-effect-kinds [--report-arg <kind>]` (Gitea #806): every effect KIND the ITEM catalog's
	// GameAction region carries, and what the import does with it.
	//
	// The twin of `--report feat-effect-kinds`, which cannot answer this: feat and spell templates are PLAINTEXT
	// under `EntriesOfType`, while an item template is TEA-encrypted under the item's own ProtoDecryptionKey, so the
	// item census has to walk the keyed `Items` rows and decrypt each one the way `region-stat-census` does. And
	// `region-stat-census` cannot answer it either - it censuses by STAT, which is the one axis that says nothing
	// about how to read the amount.
	//
	// ## The question
	//
	// `ItemTemplateStatModifiers`' own remarks state that "item templates use one kind throughout" and
	// `RdbItemImporter` relies on it: it takes `Parse` (the POSITIONAL pair view - payload words 0 and 1) and stores
	// word 1 as a flat `ItemStatModifier.Amount`, with no kind test and no kind column. Two ways that goes wrong,
	// and this report measures both:
	//
	//   1. **A non-flat KIND.** 53067 `ModifyAsRating` is a percent stored x100, so reading it flat turns "+2%
	//      Critical Rating" into "+200" - the same bug class #805 fixed on the spell side and #597 on the feat side.
	//   2. **A kind whose stat is not payload word 0.** The pair view is only (stat, magnitude) for the kinds whose
	//      key list opens with key 0; for the rest it files another slot's value as a stat id. That is what #701
	//      fixed for spells by moving them onto `ParseSpellStatEffects` (key-addressed), and the item path never
	//      moved.
	//
	// So the report prints the kind census, and then re-runs the import's own selection under the key-addressed
	// reader and diffs the two - which turns "would this change anything?" from an argument into a row count.
	public static partial class ReportCli
	{
		private static void ItemEffectKinds(string rdbDir, string? arg)
		{
			var only = uint.TryParse(arg, out var k) ? k : 0u;

			using var db = OpenDb();
			// AsNoTracking: `Item.Key` is an OWNED type, and a tracking query cannot project one without its owner.
			var items = db.Items.AsNoTracking().Where(i => i.Key.Key1 != 0 || i.Key.Key2 != 0 || i.Key.Key3 != 0 || i.Key.Key4 != 0)
				.OrderBy(i => i.Id).Select(i => new { i.Id, i.Name, i.Key }).ToList();
			using var rdb = OpenRdb(rdbDir);

			// Per kind: elements, distinct items, and one sample (item id + name + the whole payload).
			var seen = new Dictionary<uint, (int Elements, HashSet<uint> Items, uint ItemId, string ItemName, ItemTemplateStatModifiers.Element Sample)>();
			var samples = new List<(uint Id, string Name, ItemTemplateStatModifiers.Element Element)>();

			// The import diff: (itemId, statId) -> what each reader would store.
			var pairView = new Dictionary<(uint Item, uint Stat), (uint Kind, long Amount)>();
			var keyView = new Dictionary<(uint Item, uint Stat), (uint Kind, long Amount)>();

			var scanned = 0;
			foreach (var item in items)
			{
				var key = new[] { item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4 };
				byte[]? template = null;
				foreach (var type in new uint[] { 1000020, 1000620 })
				{
					if (!rdb.TryGet(type, item.Id, out var e) || e.Size == 0 || !rdb.CanRead(e))
						continue;
					try
					{
						template = rdb.ReadDecrypted(e, key);
						break;
					}
					catch
					{
						// a wrong/stale key - skipped, like the sibling reports
					}
				}

				if (template is not { Length: >= 16 })
					continue;

				scanned++;
				var body = template.AsSpan(12);

				foreach (var element in ItemTemplateStatModifiers.ParseElements(body))
				{
					if (only != 0 && element.Kind != only)
						continue;

					if (seen.TryGetValue(element.Kind, out var prior))
					{
						prior.Items.Add(item.Id);
						seen[element.Kind] = (prior.Elements + 1, prior.Items, prior.ItemId, prior.ItemName, prior.Sample);
					}
					else
					{
						seen[element.Kind] = (1, [item.Id], item.Id, item.Name ?? "", element);
					}

					if (only != 0)
						samples.Add((item.Id, item.Name ?? "", element));
				}

				// The import's OWN selection, both ways. Same whitelist, same last-write-wins, so the only difference
				// between the two dictionaries is which reader produced the (stat, amount) pair.
				foreach (var m in ItemTemplateStatModifiers.Parse(body))
					if (RdbItemImporter.ApplicableStatIds.Contains(m.StatId))
						pairView[(item.Id, m.StatId)] = (m.Kind, m.WireAmount);

				foreach (var m in ItemTemplateStatModifiers.ParseSpellStatEffects(body))
					if (RdbItemImporter.ApplicableStatIds.Contains(m.StatId))
						keyView[(item.Id, m.StatId)] = (m.Kind, m.WireAmount);
			}

			Console.WriteLine($"item-effect-kinds: {scanned} of {items.Count} keyed item templates decrypted, {seen.Count} distinct kinds");
			Console.WriteLine();
			Console.WriteLine($"  {"kind",-7} {"name",-34} {"elems",7} {"items",7}  keys / pair-view verdict");

			foreach (var (kind, hit) in seen.OrderByDescending(s => s.Value.Elements))
			{
				var type = GameActionTypes.Get(kind);
				var keys = type == null ? "<unregistered>" : $"[{string.Join(", ", type.Keys)}]";
				Console.WriteLine($"  {kind,-7} {type?.Name ?? "<unregistered>",-34} {hit.Elements,7} {hit.Items.Count,7}  {keys}");

				var statSlot = type?.SlotOf(GameActionTypes.KeyStat) ?? -1;
				var magnitudeSlot = statSlot >= 0 && type != null && statSlot + 1 < type.Keys.Length ? statSlot + 1 : -1;
				var verdict = statSlot == 0 && magnitudeSlot == 1
					? "pair view OK (Stat @0, magnitude @1)"
					: $"⚠ MISREAD by the pair view: Stat @{(statSlot < 0 ? "none" : statSlot.ToString())}, magnitude @{(magnitudeSlot < 0 ? "none" : magnitudeSlot.ToString())}";
				Console.WriteLine($"  {"",-7} {"",-34} {"",7} {"",7}  {verdict}");
				Console.WriteLine($"  {"",-7} {"",-34} {"",7} {"",7}  e.g. item {hit.ItemId} \"{hit.ItemName}\": {Payload(hit.Sample)}");
			}

			// The kinds the SERVER would have to branch on if it stored them. Anything outside this set is either not
			// a stat modifier at all or is one the aggregator cannot sum flat.
			var flat = new HashSet<uint> { 53045, 53189, 53220, 53221 };
			var nonFlat = seen.Keys.Where(kind => !flat.Contains(kind)).OrderBy(kind => kind).ToList();

			Console.WriteLine();
			Console.WriteLine("--- what the import stores, read both ways (the whitelist and the last-write-wins rule are the import's own):");
			Console.WriteLine($"  positional pair view (shipping)  : {pairView.Count} (item, stat) rows");
			Console.WriteLine($"  key-addressed view   (#701 rule) : {keyView.Count} (item, stat) rows");

			var onlyPair = pairView.Keys.Except(keyView.Keys).ToList();
			var onlyKey = keyView.Keys.Except(pairView.Keys).ToList();
			var differ = pairView.Keys.Intersect(keyView.Keys).Where(kk => pairView[kk].Amount != keyView[kk].Amount).ToList();

			Console.WriteLine($"  rows only the pair view emits    : {onlyPair.Count}");
			Console.WriteLine($"  rows only the key view emits     : {onlyKey.Count}");
			Console.WriteLine($"  rows both emit with a DIFFERENT amount: {differ.Count}");

			foreach (var kk in onlyPair.Take(20))
				Console.WriteLine($"      pair-only  item {kk.Item,10} stat {(CharacterStatId)kk.Stat,-22} kind {pairView[kk].Kind} amount {pairView[kk].Amount}");
			foreach (var kk in onlyKey.Take(20))
				Console.WriteLine($"      key-only   item {kk.Item,10} stat {(CharacterStatId)kk.Stat,-22} kind {keyView[kk].Kind} amount {keyView[kk].Amount}");
			foreach (var kk in differ.Take(20))
				Console.WriteLine($"      differs    item {kk.Item,10} stat {(CharacterStatId)kk.Stat,-22} kind {pairView[kk].Kind} pair {pairView[kk].Amount} vs key {keyView[kk].Amount}");

			Console.WriteLine();
			Console.WriteLine(nonFlat.Count == 0
				? "VERDICT: every kind in the item region is a FLAT modifier - `ItemStatModifier.Amount` needs no kind column today."
				: $"VERDICT: {nonFlat.Count} kind(s) are NOT flat modifiers: {string.Join(", ", nonFlat.Select(kind => $"{kind} {GameActionTypes.Get(kind)?.Name ?? "<unregistered>"}"))}");

			// The kinds that actually reach `ItemStatModifier` - the subset of the census the import's whitelist and
			// stat-range bound let through. A non-flat kind the whitelist already drops is not a live bug.
			var storedKinds = pairView.Values.Select(v => v.Kind).Distinct().OrderBy(kind => kind).ToList();
			Console.WriteLine($"  kinds that reach ItemStatModifier: {string.Join(", ", storedKinds.Select(kind => $"{kind} {GameActionTypes.Get(kind)?.Name ?? "<unregistered>"}"))}");

			if (samples.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"--- every {only} ({GameActionTypes.Get(only)?.Name ?? "<unregistered>"}) element, by key:");
				foreach (var (id, name, element) in samples.OrderBy(s => s.Name).ThenBy(s => s.Id))
					Console.WriteLine($"  {id,10} {name,-44} {Payload(element)}  ({element.WidthBytes}B)");
			}

			return;

			// One element as `key=value` pairs, in key order - the same rendering `feat-effect-kinds` uses.
			static string Payload(ItemTemplateStatModifiers.Element element)
			{
				return element.Type is not { } t
					? string.Join(" ", element.Values.Select((v, i) => $"[{i}]={v}"))
					: string.Join(" ", t.Keys.Select((key, i) => i >= element.Values.Length
						? $"{key}=-"
						: element.Texts[i] is { } text
							? $"{key}=\"{text}\""
							: $"{key}={element.Values[i]}"));
			}
		}
	}
}

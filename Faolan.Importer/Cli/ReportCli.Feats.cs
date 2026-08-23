using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// List every (type, id) entry in the RDB index that has the given id, with availability. Used to discover what
		// id-space a cross-reference lives in (e.g. a character's HeadMesh / *hairmesh resource id) (#275).
		// The feat/ability catalog's RDB type.
		private const uint FeatTemplateType = 1000020;

		// --report feat-table [--report-arg <value>]
		//
		// All TWELVE words of every entry in the client's feat table (RDB 1000042 id 1). The importer reads six of
		// them - (wire, template), the three prereq columns, the root flag and the base index - and #590 needs to know
		// what the other six hold, because a feat's modifier record references a target id in a ~20000-45000 space
		// that resolves against nothing we import. With `--report-arg <value>` it reports which COLUMN, if any, holds
		// that value - which is the whole question.
		private static void FeatTable(string rdbDir, string? arg)
		{
			var needle = uint.TryParse(arg, out var v) ? v : 0u;
			using var rdb = OpenRdb(rdbDir);
			var matches = rdb.EntriesOfType(1000042u).Where(e => e.Id == 1).ToList();
			if (matches.Count == 0)
			{
				Console.WriteLine("feat-table: RDB 1000042 id 1 not present");
				return;
			}

			var body = rdb.ReadDecompressed(matches[0]);
			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12));
			Console.WriteLine($"feat table: {count} entries, {body.Length} bytes");

			const int header = 20, stride = 48, words = 12;
			var columnHits = new int[words];
			var perColumnRange = new (uint Min, uint Max)[words];
			for (var c = 0; c < words; c++)
				perColumnRange[c] = (uint.MaxValue, 0);

			var shown = 0;
			for (var i = 0; i < count; i++)
			{
				var at = header + (i * stride);
				if (at + stride > body.Length)
					break;

				var w = new uint[words];
				for (var c = 0; c < words; c++)
					w[c] = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + (c * 4)));

				for (var c = 0; c < words; c++)
				{
					if (w[c] == 0)
						continue;

					perColumnRange[c] = (Math.Min(perColumnRange[c].Min, w[c]), Math.Max(perColumnRange[c].Max, w[c]));
					if (needle != 0 && w[c] == needle)
					{
						columnHits[c]++;
						if (shown++ < 12)
							Console.WriteLine($"  hit col[{c}] entry {i}: " + string.Join(' ', w.Select(x => x.ToString().PadLeft(9))));
					}
				}

				if (needle == 0 && i < 6)
					Console.WriteLine($"  entry {i}: " + string.Join(' ', w.Select(x => x.ToString().PadLeft(9))));
			}

			Console.WriteLine();
			Console.WriteLine("per-column non-zero range:");
			for (var c = 0; c < words; c++)
			{
				var known = c switch
				{
					0 => " (wire id)", 1 => " (template id)", 2 or 3 or 4 => " (prereq)", 8 => " (root flag)", 10 => " (base index)", _ => ""
				};
				var range = perColumnRange[c].Min == uint.MaxValue ? "all zero" : $"{perColumnRange[c].Min}..{perColumnRange[c].Max}";
				Console.WriteLine($"  col[{c,2}] {range}{known}" + (needle != 0 ? $"   hits={columnHits[c]}" : ""));
			}
		}

		// --report feat-spell-modifiers [--report-arg <kind>]
		//
		// #590/#591. Every feat template's modifier records whose payload's first word is OUTSIDE the stat-id range -
		// exactly the ones ItemTemplateStatModifiers.Parse drops on `statId >= 0x1000`. That bound is right for a STAT
		// modifier and wrong for whatever these are: `Decomposition` rank 5 carries (41000, 3500) under kind 53075,
		// and 3500 is the +35.00% its in-client tooltip shows.
		//
		// The KIND is a GameAction type id and the payload is that type's KEY LIST, one u32 per key
		// (docs/re/gameactionlist-grammar.md), so this prints each row as its named slots rather than as a pair -
		// `SpellStatModifier` puts the spell first and the STAT second, which read positionally looked like a
		// magnitude that varied for no reason.
		private static void FeatSpellModifiers(string rdbDir, string? arg)
		{
			var onlyKind = uint.TryParse(arg, out var k) ? k : 0u;
			using var rdb = OpenRdb(rdbDir);
			var byKind = new Dictionary<uint, int>();
			var samples = new Dictionary<uint, List<string>>();
			var refCounts = new Dictionary<uint, int>();
			var feats = 0;

			// A payload slot printed as `key=value`, with the client's own name for the key where it has one.
			static string Slots(ItemTemplateStatModifiers.Element e)
			{
				var type = e.Type;
				if (type == null)
					return string.Join(' ', e.Values.Select((v, i) => $"[{i}]={v}"));

				return string.Join(' ', e.Values.Select((v, i) =>
				{
					var key = type.Keys[i];
					var name = key switch
					{
						GameActionTypes.KeyStat => "Stat",
						GameActionTypes.KeyLine => "Line",
						GameActionTypes.KeyValue => "Value",
						GameActionTypes.KeyAmount => "Amount",
						GameActionTypes.KeySpell => "Spell",
						_ => $"k{key}"
					};
					return $"{name}={v}";
				}));
			}

			foreach (var e in rdb.EntriesOfType(FeatTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != FeatTemplateType)
					continue;

				// The out-of-stat-range half, kept whole rather than as a pair - the same elements ParseRejected
				// yields, with every slot the kind declares.
				var hits = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12))
					.Where(x => x.Values.Length > 0 && x.Values[0] >= 0x1000)
					.ToList();
				if (hits.Count == 0)
					continue;

				feats++;
				var (name, description) = RdbFeatImporter.ReadNameAndDescription(body);
				foreach (var hit in hits)
				{
					var kind = hit.Kind;
					if (onlyKind != 0 && kind != onlyKind)
						continue;

					byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
					refCounts[hit.Values[0]] = refCounts.GetValueOrDefault(hit.Values[0]) + 1;
					samples.TryAdd(kind, []);
					// With a kind filter the caller is decoding THAT kind, so print every row and its description -
					// the description names the spell the feat modifies, which is the only handle we have on what the
					// reference id means. Unfiltered it is a census, so a few samples per kind is enough.
					if (onlyKind != 0 || samples[kind].Count < 6)
						samples[kind].Add($"{e.Id}\t{name}\t{Slots(hit)}\t{description.Replace('\t', ' ')}");
				}
			}

			Console.WriteLine($"feat templates carrying an out-of-stat-range modifier: {feats}");
			Console.WriteLine();
			Console.WriteLine("by KIND:");
			foreach (var (kind, n) in byKind.OrderByDescending(p => p.Value))
			{
				var type = GameActionTypes.Get(kind);
				Console.WriteLine($"  {kind} (0x{kind:X})  x{n}  {type?.Name ?? "<unregistered>"}");
				foreach (var line in samples[kind])
					Console.WriteLine($"      {line}");
			}

			Console.WriteLine();
			Console.WriteLine("most-referenced targets:");
			foreach (var (reference, n) in refCounts.OrderByDescending(p => p.Value).Take(20))
				Console.WriteLine($"  ref {reference} x{n}");
		}

		// --report feat-record --report-arg [<rdbType>/]<id>[,<id>...]
		//
		// The RAW words of one or more feat templates, side by side. Built for #590: a feat like `Decomposition`
		// carries "+35% to Flesh to Worms" that the stat-modifier region does not hold, so finding where the client
		// keeps it means diffing the five RANKS of one feat against each other - the words that MOVE across ranks are
		// the magnitude, the ones that stay are the target. Passing several ids prints a per-word diff column for
		// exactly that.
		private static void FeatRecord(string rdbDir, string? arg)
		{
			// An optional `<type>/` prefix dumps a record of ANOTHER RDB type with the same machinery - spell
			// templates (1040005) share the feat/item record layout, and #590 needs to compare the two.
			var spec = (arg ?? "").Split('/', 2, StringSplitOptions.TrimEntries);
			var type = spec.Length == 2 && uint.TryParse(spec[0], out var t) ? t : FeatTemplateType;
			var ids = spec[^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(x => uint.TryParse(x, out var v) ? v : 0).Where(v => v != 0).ToArray();
			if (ids.Length == 0)
			{
				Console.WriteLine("feat-record: pass one or more template ids, e.g. --report-arg 1173623,1173628");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var bodies = new Dictionary<uint, byte[]>();
			foreach (var e in rdb.EntriesOfType(type))
			{
				if (e.Size == 0 || !ids.Contains(e.Id))
					continue;

				try
				{
					bodies[e.Id] = rdb.ReadDecompressed(e);
				}
				catch (Exception ex)
				{
					Console.WriteLine($"  {e.Id}: unreadable ({ex.GetType().Name})");
				}
			}

			foreach (var id in ids)
			{
				if (!bodies.TryGetValue(id, out var body))
				{
					Console.WriteLine($"{id}: not present in RDB type {type}");
					continue;
				}

				var (name, description) = RdbFeatImporter.ReadNameAndDescription(body);
				var (start, count) = RdbFeatImporter.EffectTail(body);
				Console.WriteLine($"=== {id}  {body.Length} bytes  name=\"{name}\"  effects={count} (at {start})");
				Console.WriteLine($"    desc: {description}");
			}

			// A hex dump per record. The text is variable-length, so everything after it is NOT 4-aligned to the
			// record start - the word grid below will straddle the real fields, and this is what lets a reader
			// re-cut them by eye.
			foreach (var id in ids.Where(bodies.ContainsKey))
			{
				var body = bodies[id];
				Console.WriteLine();
				Console.WriteLine($"--- {id} hex");
				for (var off = 0; off < body.Length; off += 16)
				{
					var run = Math.Min(16, body.Length - off);
					var hex = string.Join(' ', Enumerable.Range(0, run).Select(k => body[off + k].ToString("x2")));
					var ascii = string.Concat(Enumerable.Range(0, run).Select(k => body[off + k] is >= 0x20 and < 0x7F ? (char)body[off + k] : '.'));
					Console.WriteLine($"  {off,4:d}  {hex.PadRight(47)}  {ascii}");
				}
			}

			// The word-by-word view. Only the words the records DISAGREE on are interesting when several are given,
			// so those are flagged - that is the whole point of passing a feat's five ranks at once.
			var present = ids.Where(bodies.ContainsKey).ToArray();
			if (present.Length == 0)
				return;

			var longest = present.Max(i => bodies[i].Length);
			Console.WriteLine();
			Console.WriteLine("offset   " + string.Join("  ", present.Select(i => i.ToString().PadLeft(10))) + "   diff");
			for (var off = 0; off + 4 <= longest; off += 4)
			{
				var words = present.Select(i => bodies[i].Length >= off + 4
					? BinaryPrimitives.ReadUInt32LittleEndian(bodies[i].AsSpan(off))
					: (uint?)null).ToArray();
				var differs = words.Distinct().Count() > 1;
				Console.WriteLine($"  [{off,4}]  "
					+ string.Join("  ", words.Select(w => (w?.ToString() ?? "-").PadLeft(10)))
					+ (differs ? "   <<<" : ""));
			}
		}

		private static void FeatEffectWidths(string rdbDir, string? arg)
		{
			var templateType = uint.TryParse(arg, out var t) && t != 0 ? t : FeatTemplateType;
			using var rdb = OpenRdb(rdbDir);
			var widths = new Dictionary<uint, Dictionary<int, int>>();
			var surplus = new Dictionary<uint, (uint Id, uint[] Words)>();
			var tiled = new Dictionary<uint, Dictionary<int, (int Count, uint First, uint[] Extra)>>();
			var regions = 0;
			var framed = 0;
			var frameAgrees = 0;
			var singles = 0;
			var multi = 0;

			foreach (var e in rdb.EntriesOfType(templateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != templateType)
					continue;

				// ---- Measurement 2: TILING (#595) ----
				// The pass below derives a kind's width from records holding exactly ONE element, where the only
				// available end marker is the record's, so what it measures is a CEILING - it swallows whatever the
				// record carries after the element list. Elements sit back to back with nothing between them, so a
				// record holding SEVERAL measures the non-final ones EXACTLY: the distance to the next element's
				// opening type word IS that element's width. That is an independent measurement, and it is the one
				// that sees the `Swift Pathing` case the ceiling pass is structurally blind to (two elements, both
				// short, in a record with no single-element sibling).
				//
				// The final element is deliberately NOT tallied: its end is the record's, so it is a ceiling like the
				// pass below, and mixing the two would let trailing record content masquerade as element bytes.
				var located = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12));
				if (located.Count > 0)
				{
					regions++;

					// The list frame sits immediately before the first element's type word - the same `0x3F1` count
					// word every other list in the protocol carries. Reading it here is what turns "the scan found N
					// signature hits" into "the record SAYS there are N", so a spurious hit shows up as disagreement
					// instead of quietly widening a histogram.
					var frameOff = 12 + located[0].Offset - 4;
					var declared = frameOff >= 0 && ListFrame.TryCountOf(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(frameOff)), out var c) ? c : -1;
					if (declared >= 0)
						framed++;

					// Tally only where the record's own count agrees with what the scan found. Where it does not, the
					// scan has either crossed out of the element region (into trailing record content that happens to
					// carry the signature) or missed one, and the distance between two "consecutive" elements is then
					// a GAP, not a width - which is where the non-multiple-of-4 "widths" (43B, 47B, 53B...) come
					// from. Agreement is the cheap, record-supplied proof that a pair really is adjacent.
					if (declared == located.Count)
					{
						frameAgrees++;
						for (var i = 0; i + 1 < located.Count; i++)
						{
							var w = located[i + 1].Offset - located[i].Offset;
							tiled.TryAdd(located[i].Kind, []);
							var seenSoFar = tiled[located[i].Kind];
							var prior = seenSoFar.GetValueOrDefault(w);

							// The words a measured element carries PAST its key list. Same reason the ceiling pass
							// prints them: "ChangeHitPercent disagrees" is a shrug, "ChangeHitPercent is followed by
							// four 0xFF words - the key list's own terminator value" is a lead.
							var want = GameActionTypes.Get(located[i].Kind) is { IsFixedWidth: true } ft ? 4 * ft.WidthWords : 0;
							var extra = prior.Count == 0 && want > 0 && w > want
								? Enumerable.Range(0, (w - want) / 4)
									.Select(k => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12 + located[i].Offset + want + 4 * k))).ToArray()
								: prior.Extra;
							seenSoFar[w] = (prior.Count + 1, prior.Count == 0 ? e.Id : prior.First, extra);
						}
					}
				}

				var (start, count) = RdbFeatImporter.EffectTail(body);
				if (count <= 0 || start + 4 > body.Length)
					continue;

				if (count > 1)
				{
					multi++;
					continue;
				}

				singles++;
				var kind = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start));
				var width = body.Length - start;
				widths.TryAdd(kind, new Dictionary<int, int>());
				widths[kind][width] = widths[kind].GetValueOrDefault(width) + 1;

				// A measured width WIDER than the key list predicts is bytes past the element, and this is what says
				// WHAT they are rather than only that they exist - the difference between "AddSkinnedMesh disagrees"
				// and "AddSkinnedMesh is followed by `2 9 207912`". Both surpluses seen so far are trailing RECORD
				// content, not element bytes: `Modify`'s is a pair of list frames (0x7E2, 0x3F1) and
				// `SetMorphValue`'s is an ASCII mesh name.
				var fixedWidth = GameActionTypes.Get(kind) is { IsFixedWidth: true } fw ? fw : null;
				var predicted = 4 * (fixedWidth?.WidthWords ?? 0);
				if (predicted > 0 && width > predicted && !surplus.ContainsKey(kind))
					surplus[kind] = (e.Id, [.. Enumerable.Range(0, (width - predicted) / 4)
						.Select(i => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(start + predicted + 4 * i)))]);
			}

			Console.WriteLine($"single-element records: {singles}   multi-element: {multi}");
			Console.WriteLine("kind -> width(s) observed in single-element records, vs the width its KEY LIST predicts:");
			foreach (var (kind, seen) in widths.OrderByDescending(k => k.Value.Values.Sum()))
			{
				var type = GameActionTypes.Get(kind);
				var predicted = type == null ? "<unregistered>" : type.IsFixedWidth ? $"{4 * type.WidthWords}B {type.Name}" : $"VARIABLE (a text slot) {type.Name}";
				var agrees = type == null || !type.IsFixedWidth || seen.Keys.Contains(4 * type.WidthWords) ? "" : "   <-- DISAGREES";
				Console.WriteLine($"  0x{kind:X4}  {string.Join(", ", seen.OrderByDescending(w => w.Value).Select(w => $"{w.Key}B x{w.Value}"))}   predicted {predicted}{agrees}");
				if (surplus.TryGetValue(kind, out var extra))
					Console.WriteLine($"            surplus in {extra.Id}: {string.Join(" ", extra.Words.Select(w => $"{w} (0x{w:X})"))}");
			}

			Console.WriteLine();
			Console.WriteLine($"element regions: {regions}   with a readable 0x3F1 frame: {framed}   whose count matches the elements found: {frameAgrees}");
			Console.WriteLine("kind -> width(s) MEASURED by tiling (an element's start to the NEXT element's start), vs the width its KEY LIST predicts:");
			foreach (var (kind, seen) in tiled.OrderByDescending(k => k.Value.Values.Sum(v => v.Count)))
			{
				var type = GameActionTypes.Get(kind);
				var predicted = type == null ? "<unregistered>" : type.IsFixedWidth ? $"{4 * type.WidthWords}B {type.Name}" : $"VARIABLE (a text slot) {type.Name}";
				var agrees = type == null || !type.IsFixedWidth || seen.Keys.All(w => w == 4 * type.WidthWords) ? "" : "   <-- DISAGREES";
				Console.WriteLine($"  0x{kind:X4}  {string.Join(", ", seen.OrderByDescending(w => w.Value.Count).Select(w => $"{w.Key}B x{w.Value.Count} (e.g. {w.Value.First})"))}   predicted {predicted}{agrees}");
				foreach (var (width, hit) in seen.Where(w => w.Value.Extra is { Length: > 0 }))
					Console.WriteLine($"            {width}B carries {hit.Extra.Length} word(s) past the key list, in {hit.First}: {string.Join(" ", hit.Extra.Select(w => $"{w} (0x{w:X})"))}");
			}
		}

		// --report feat-effect-kinds [--report-arg <kind>]
		//
		// Every effect KIND the feat catalog actually carries, with the key list the client registers for it, and -
		// the point of the report - what the two readings of the same element DISAGREE about (Gitea #597).
		//
		// `ItemTemplateStatModifiers.Modifier` is the PAIR view: payload words 0 and 1, whatever they mean. That is
		// (stat, value) for `Modify` and for nothing else in particular - `InterpolatedModify` puts a Substate in word
		// 1 and its Amount in word 3, `TargetSpellLineStatModifier` puts a spell LINE in word 0 and the stat in word
		// 1, `AddPercentSkillModifier` has no stat slot at all. So the report prints, per kind, the pair a positional
		// read yields beside the values the KEY-addressed read yields for the same element, and flags every kind where
		// they differ. A kind whose stat/amount keys are not in slots 0/1 is one the pair view cannot import.
		private static void FeatEffectKinds(string rdbDir, string? arg)
		{
			// `<type>/<kind>` - the same spelling `feat-record` uses. Spell templates (1040005) share the record
			// layout and the same region parser, so the same census answers "does this kind read the same way for a
			// spell", which is what ties a decode to the in-client tooltips we have for spells.
			var spec = (arg ?? "").Split('/', 2, StringSplitOptions.TrimEntries);
			var templateType = spec.Length == 2 && uint.TryParse(spec[0], out var t) ? t : FeatTemplateType;
			var only = uint.TryParse(spec[^1], out var k) ? k : 0u;
			using var rdb = OpenRdb(rdbDir);

			// Per kind: how many elements carry it, one sample (feat id + name + the whole payload), and whether the
			// pair view and the key-addressed view agree on that sample.
			var seen = new Dictionary<uint, (int Count, uint FeatId, string FeatName, ItemTemplateStatModifiers.Element Sample)>();
			// Every distinct payload of the FILTERED kind, so a single-kind run reads as a census of that kind's
			// slots rather than one sample - which is what makes "slot 1 tracks the tooltip, slot 3 does not"
			// checkable instead of asserted.
			var samples = new List<(uint Id, string Name, ItemTemplateStatModifiers.Element Element, string Raw)>();
			var textAudit = new Dictionary<string, List<(uint Id, string Name, uint Kind, string Bytes)>>();

			foreach (var e in rdb.EntriesOfType(templateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue;
				}

				if (body.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(body) != templateType)
					continue;

				var name = RdbFeatImporter.ReadNameAndDescription(body).Name;
				var located = ItemTemplateStatModifiers.ParseElements(body.AsSpan(12));
				for (var i = 0; i < located.Count; i++)
				{
					// A text-carrying element whose successor is known: its measured width settles the length word.
					if (located[i].Type is not { IsFixedWidth: false } tt || i + 1 >= located.Count)
						continue;

					var measured = located[i + 1].Offset - located[i].Offset;
					var texts = tt.Keys.Count(k => GameActionTypes.TextKeys.Contains(k));
					var fixedSlots = tt.Keys.Length - texts;
					// 32-byte prefix + one u32 per non-text slot + per text slot either [u32 len][len bytes] or
					// [u16 len][len bytes]. The string BYTES are the same either way, so the two forms differ only by
					// 2 bytes per text slot - which is exactly what the measured width can tell apart.
					var bytes = measured - 32 - 4 * fixedSlots;
					var slotStart = 12 + located[i].Offset + 32 + 4 * tt.Keys.TakeWhile(k => !GameActionTypes.TextKeys.Contains(k)).Count();
					var raw = string.Join(" ", Enumerable.Range(0, Math.Min(24, body.Length - slotStart)).Select(k => body[slotStart + k].ToString("x2")));
					var u32Len = texts == 1 && slotStart + 4 <= body.Length ? BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(slotStart)) : uint.MaxValue;
					var u16Len = texts == 1 && slotStart + 2 <= body.Length ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(slotStart)) : ushort.MaxValue;
					var form = texts != 1
						? $"{texts} text slots - not decided"
						: bytes == 4 + u32Len
							? "[u32 len][bytes]"
							: bytes == 2 + u16Len
								? "[u16 len][bytes]"
								: "NEITHER";
					textAudit.TryAdd(form, []);
					textAudit[form].Add((e.Id, name, located[i].Kind, raw));
				}

				foreach (var element in located)
				{
					if (only != 0 && element.Kind != only)
						continue;

					var prior = seen.GetValueOrDefault(element.Kind);
					seen[element.Kind] = prior.Count == 0
						? (1, e.Id, name, element)
						: (prior.Count + 1, prior.FeatId, prior.FeatName, prior.Sample);
					if (only != 0)
					{
						// The payload BYTES, from the four always-present values onward. An UNREGISTERED kind has no
						// key list, so the key=value rendering below invents two slots for it and shows nothing else -
						// the bytes are the only way to see what such a record actually holds (Gitea #596).
						var from = 12 + element.Offset + 24;
						samples.Add((e.Id, name, element,
							string.Join(" ", Enumerable.Range(0, Math.Clamp(body.Length - from, 0, 48)).Select(k => body[from + k].ToString("x2")))));
					}
				}
			}

			Console.WriteLine("kind -> key list, and what the POSITIONAL pair view reads vs the KEY-addressed one.");
			Console.WriteLine("A kind whose Stat (key 0) is not payload word 0, or whose magnitude key is not word 1, is MISREAD by the pair view.");
			Console.WriteLine();

			foreach (var (kind, hit) in seen.OrderByDescending(s => s.Value.Count))
			{
				var type = GameActionTypes.Get(kind);
				var keys = type == null ? "<unregistered>" : $"[{string.Join(", ", type.Keys)}]";
				Console.WriteLine($"  {kind}  x{hit.Count,-6} {type?.Name ?? "<unregistered>",-32} keys {keys}");

				var statSlot = type?.SlotOf(GameActionTypes.KeyStat) ?? -1;
				var valueSlot = type?.SlotOf(GameActionTypes.KeyValue) ?? -1;
				var amountSlot = type?.SlotOf(GameActionTypes.KeyAmount) ?? -1;
				var magnitudeSlot = amountSlot >= 0 ? amountSlot : valueSlot;
				var verdict = statSlot == 0 && magnitudeSlot == 1
					? "pair view OK (Stat @0, magnitude @1)"
					: $"MISREAD: Stat @{(statSlot < 0 ? "none" : statSlot.ToString())}, magnitude @{(magnitudeSlot < 0 ? "none" : magnitudeSlot.ToString())}";
				Console.WriteLine($"           {verdict}");

				Console.WriteLine($"           e.g. feat {hit.FeatId} \"{hit.FeatName}\": {Payload(hit.Sample)}  ({hit.Sample.WidthBytes}B)");
				Console.WriteLine($"           pair view would import (StatId={(hit.Sample.Values.Length > 0 ? hit.Sample.Values[0] : 0)}, "
					+ $"Value={(hit.Sample.Values.Length > 1 ? hit.Sample.Values[1] : 0)})");
			}

			// The TEXT-slot audit (#596). A text slot is `[length][bytes]`, and which width that length word is has to
			// be MEASURED, not assumed: an element's true width is the distance to the next element's type word, so
			// the reading whose payload ends exactly there is the right one. Printed as a tally per (kind, width form)
			// with the raw bytes of the first disagreement, because "some records read as u16" is a shrug and "kind
			// 53261 reads `12 00 'Hash=WMTW;Level=40'` and tiles at 56B" is a lead.
			if (textAudit.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("--- text slots, by which length word makes the element tile to its measured width:");
				foreach (var (form, hits) in textAudit.OrderByDescending(a => a.Value.Count))
				{
					Console.WriteLine($"  {form} x{hits.Count}");
					foreach (var byKind in hits.GroupBy(h => h.Kind).OrderByDescending(g => g.Count()))
					{
						Console.WriteLine($"      kind {byKind.Key} {GameActionTypes.Get(byKind.Key)?.Name ?? "<unregistered>",-36} x{byKind.Count()}");
						// Up to eight members, not one. A length word is settled by the records that DISAGREE with the
						// rule, and a bucket printed as a single example cannot show whether its members agree with
						// each other - which is the whole question when a form holds six records (Gitea #596).
						foreach (var hit in byKind.Take(8))
							Console.WriteLine($"          {hit.Id,10} \"{hit.Name}\": {hit.Bytes}");
						if (byKind.Count() > 8)
							Console.WriteLine($"          ... and {byKind.Count() - 8} more");
					}
				}
			}

			if (samples.Count == 0)
				return;

			var only_ = GameActionTypes.Get(only);
			Console.WriteLine();
			Console.WriteLine($"--- every {only} ({only_?.Name ?? "<unregistered>"}) element, by key:");
			foreach (var (id, name, element, raw) in samples.OrderBy(s => s.Name).ThenBy(s => s.Id))
			{
				Console.WriteLine($"  {id,10} {name,-44} {Payload(element)}  ({element.WidthBytes}B)");
				Console.WriteLine($"             payload {raw}");
			}

			return;

			// One element as `key=value` pairs, in key order. A TEXT slot prints the STRING rather than its byte
			// length, which is what makes a string-aware read visible in the output at all.
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

		// Classify the RDB feat/ability catalog (type 1000020) into PLAINTEXT vs TEA-ENCRYPTED records, and dump the
		// plaintext ones (Gitea #251). A plaintext record opens with its own TYPE word (1000020 = 0x000F4254, which
		// renders as `TB\x0f\x00` in a dump and is NOT a magic) while an encrypted one opens with `RDBH`.
		// The plaintext share needs NO key, so it is a feat catalog we can build today.
		// --report-arg <substring>  filters the dumped names   --out <dir>  writes feat-catalog.tsv
		private static void FeatCatalog(string rdbDir, string? filter, string? outDir)
		{
			using var rdb = OpenRdb(rdbDir);
			var plain = new List<(uint Id, string Name, byte[] Body)>();
			var encrypted = 0;
			var unreadable = 0;
			var other = 0;

			foreach (var e in rdb.EntriesOfType(FeatTemplateType))
			{
				if (e.Size == 0)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					unreadable++;
					continue;
				}

				if (body.Length < 8)
				{
					other++;
					continue;
				}

				if (BinaryPrimitives.ReadUInt32LittleEndian(body) != FeatTemplateType)
				{
					// `RDBH` is the TEA-encrypted template header; anything else is a shape we have not seen.
					if (body[0] == (byte)'R' && body[1] == (byte)'D' && body[2] == (byte)'B' && body[3] == (byte)'H')
						encrypted++;
					else
						other++;

					continue;
				}

				plain.Add((e.Id, RdbFeatImporter.ReadNameAndDescription(body).Name, body));
			}

			var total = plain.Count + encrypted + other + unreadable;
			Console.WriteLine($"RDB type {FeatTemplateType}: {total} records");
			Console.WriteLine($"  plaintext (TB magic) : {plain.Count} ({(total == 0 ? 0 : 100.0 * plain.Count / total):F1}%)");
			Console.WriteLine($"  TEA-encrypted (RDBH) : {encrypted}");
			Console.WriteLine($"  other / unreadable   : {other} / {unreadable}");

			var named = plain.Where(p => p.Name.Length > 0).ToList();
			Console.WriteLine($"  plaintext WITH a name: {named.Count}");

			// --report-arg <id>[,<id>...]: dump those records' FULL word arrays and then the offsets where they
			// DIFFER. Handed a rank series (same feat, consecutive ranks) the diff isolates the rank/tier/prereq
			// fields directly, which is how #251 step 2 cross-correlates same-tree feats.
			if (!string.IsNullOrWhiteSpace(filter) && filter.Split(',').All(x => uint.TryParse(x.Trim(), out _)))
			{
				var picked = new List<(uint Id, string Name, byte[] Body)>();
				foreach (var want in filter.Split(',').Select(x => uint.Parse(x.Trim())))
				{
					var rec = plain.FirstOrDefault(x => x.Id == want);
					if (rec.Body == null)
					{
						Console.WriteLine($"    id={want}: not a plaintext 1000020 record");
						continue;
					}

					picked.Add(rec);
					var words = new List<string>();
					for (var o = 0; o + 4 <= rec.Body.Length; o += 4)
						words.Add($"[{o,3}]={BinaryPrimitives.ReadUInt32LittleEndian(rec.Body.AsSpan(o))}");

					Console.WriteLine($"    id={rec.Id} len={rec.Body.Length} name='{rec.Name}'");
					Console.WriteLine($"      {string.Join(" ", words)}");
					Console.WriteLine($"      hex={Convert.ToHexString(rec.Body)}");
				}

				if (picked.Count > 1)
				{
					// Two alignments, because the NAME sits in the middle and its length varies: absolute offsets are
					// comparable only for equal-length names, while everything after the name lines up when measured
					// from the tail's start. Cross-FEAT comparison needs the tail view; a rank series can use either.
					Console.WriteLine("  HEADER diffs (absolute offset, valid while the names are the same length):");
					var headWords = picked.Min(p => TailStart(p.Body)) / 4;
					for (var wi = 0; wi < headWords; wi++)
					{
						var vals = picked.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(wi * 4))).ToList();
						if (vals.Distinct().Count() > 1)
							Console.WriteLine($"    [{wi * 4,3}] {string.Join(" -> ", vals)}");
					}

					Console.WriteLine("  TAIL words (offset RELATIVE to the end of the name; '=' means identical across the set):");
					var tailWords = picked.Min(p => (p.Body.Length - TailStart(p.Body)) / 4);
					for (var wi = 0; wi < tailWords; wi++)
					{
						var vals = picked.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(TailStart(p.Body) + (wi * 4)))).ToList();
						var same = vals.Distinct().Count() == 1;
						Console.WriteLine($"    +{wi * 4,-3} {(same ? "=" : " ")} {string.Join(" -> ", vals)}");
					}
				}

				return;
			}

			// Type 1000020 carries ITEMS and FEATS in one type, so the catalog is only useful with the discriminating
			// words alongside each name. Two look decisive on inspection - [28] (a category id: 7063 on feats,
			// 9081/10090 on the item templates checked) and [44] (0x10000005 vs 512) - but the rule is NOT hardcoded
			// here: the words go out as columns and the distribution is printed, so the split is chosen from the whole
			// catalog rather than from a handful of samples.
			var byFlags = named.GroupBy(p => Word(p.Body, 44)).OrderByDescending(g => g.Count()).ToList();
			Console.WriteLine("  [44] distribution (named records):");
			foreach (var g in byFlags.Take(8))
				Console.WriteLine($"    0x{g.Key:X8} x{g.Count(),-6} e.g. {g.First().Name}");

			var shown = named.Where(p => string.IsNullOrWhiteSpace(filter) || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
			foreach (var p in shown)
				Console.WriteLine($"    {p.Id,-9} cat={Word(p.Body, 28),-6} flags=0x{Word(p.Body, 44):X8} {p.Name}");

			if (string.IsNullOrWhiteSpace(outDir))
				return;

			Directory.CreateDirectory(outDir);
			var path = Path.Combine(outDir, "feat-catalog.tsv");
			using var w = new StreamWriter(path);
			w.WriteLine("id\tlength\tcategory\tflags\tname");
			foreach (var p in named.OrderBy(p => p.Id))
				w.WriteLine($"{p.Id}\t{p.Body.Length}\t{Word(p.Body, 28)}\t0x{Word(p.Body, 44):X8}\t{p.Name}");

			Console.WriteLine($"WROTE {path} ({named.Count} rows)");
		}

		// Where the record's TAIL begins: just past the name. Everything before it is the fixed header, everything
		// after is the variable part, and only the tail lines up between records whose names differ in length.
		private static int TailStart(byte[] body)
		{
			for (var i = 8; i + 4 < body.Length; i += 4)
			{
				var len = Word(body, i);
				if (len is < 2 or > 96 || i + 4 + len > body.Length)
					continue;

				var printable = true;
				foreach (var b in body.AsSpan(i + 4, (int)len))
				{
					if (b is < 0x20 or > 0x7E)
					{
						printable = false;
						break;
					}
				}

				if (printable)
					return i + 4 + (int)len;
			}

			return body.Length;
		}
	}
}

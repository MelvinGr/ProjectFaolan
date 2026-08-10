using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Faolan.Core.Crypt;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report spell-tooltip --report-arg <spellId>[,<spellId>...]` (#775): render a spell's in-client TOOLTIP out of
	// our own decode, beside the raw record it came from.
	//
	// ## Why a tooltip is the gate
	//
	// The mechanics region of a spell record is structurally complete (0 unparsed element heads over all 20,847
	// records, 0 unregistered kinds - #772/#774), which says we can SEE every mechanic a spell has. It does not say we
	// can read one. A tooltip is the strongest available accounting check on that second question, for the same reason
	// a codec round-trip is: it consumes nearly everything a record holds, and a screenshot is an unarguable oracle.
	//
	// The renderer is deliberately the CLIENT'S, not ours:
	//
	//   * the per-kind SENTENCE is LDB category 170, keyed by the GameAction type id - `53088` really is
	//     `%s %.2fm%s radius up to %d%s targets`. Recovered here (#775); nothing had read that category before, and
	//     it is what turns a decoded element into a line of English;
	//   * the stat NAME is LDB category 615 keyed by the wire stat id;
	//   * the DIVISOR is `StatDisplayScale` (the client's own tooltip arithmetic, #251);
	//   * the UNIT is `StatUnitSuffix` (the client's own suffix helper, #775) - and it is NOT derivable from the
	//     divisor: `0x348 DamageMultiplier` is a x1 that draws `%`, `0x001 HealthMax` a x100 that draws nothing.
	//
	// ## What it reproduces
	//
	// `Gangrenous Stench` (221070) is the worked case, against the in-client screenshots on #772:
	//
	//     -10% Slashing / Piercing / Crushing / Poison Invulnerability   (4x `Modify` -1000, stat 902/903/904/909,
	//                                                                     divisor 100, `%` suffix)
	//     -5% Damage Multiplier                                          (`Modify` -5, stat 840, divisor 1, `%`)
	//     7.00m radius up to 12 targets                                  (`FilteredAreaFormula` 11=7 170=12)
	//     60 seconds recast                                              (`SetSpellRecastTime` 39=6000)
	//
	// ⚠️ **and the four invulnerabilities are not on the spell**. They live two links down the chain - 221070's area
	// element points at 221069, whose `CastNano` points at 3963058, which is where the -1000s are. A renderer that
	// does not FOLLOW the chain reproduces none of the line the player actually reads, which is precisely why the
	// tooltip is a better gate than any per-record assertion: it fails unless the whole graph is walked.
	//
	// ## What it does NOT claim
	//
	// A slot whose binding is not established prints as its raw key list beside the unfilled format string, never as
	// a guess - the point is an honest diff against a screenshot, and a plausible wrong number is worse than a
	// visible gap. Three known ones, tracked rather than papered over:
	//
	//   * the MANA cost is usually a percent of the caster's pool (#629), so the client's "913 mana" is a number this
	//     report cannot produce from the record alone - it prints the mode and the stored value;
	//   * a LEVEL RAMP needs the caster's level; `--report-arg <id>@<level>` supplies one, and without it the ramp
	//     prints as its two endpoints;
	//   * the property run's unread fields are listed with the client's own name for each, marked `unread`, which is
	//     the #633/#592 backlog seen per spell instead of per catalog.
	public static partial class ReportCli
	{
		// LDB category 170: the tooltip SENTENCE per GameAction type, keyed by the type id itself for the ~30 types
		// that have one (the rest of the category is keyed by ACG-hashed token names and is not element text).
		private const uint LdbGameActionFormatCategory = 170;

		// The GameAction key that carries a target CAP on the area kinds (see RdbSpellImporter.KeyMaxTargets - the
		// same key means something else on `Disarm`/`InterpolatedHit`, so it stays scoped to the binders below).
		private const int KeyAreaMaxTargets = 170;

		// How deep the effect chain is followed. A spell chain is a graph and it is cycle-guarded by id, so this is
		// only a display bound - it exists so a pathological record cannot print forever.
		private const int MaxChainDepth = 6;

		private static void SpellTooltip(string rdbDir, string? arg)
		{
			if (string.Equals((arg ?? "").Trim(), "census", StringComparison.OrdinalIgnoreCase))
			{
				SpellTooltipCensus(rdbDir);
				return;
			}

			var (ids, level) = ParseTooltipArg(arg);
			if (ids.Count == 0)
			{
				Console.WriteLine("spell-tooltip: --report-arg is <spellId>[,<spellId>...][@<casterLevel>]  e.g. 221070@80");
				Console.WriteLine("               --report-arg census scores the WHOLE catalog instead of rendering one spell.");
				return;
			}

			using var rdb = OpenRdb(rdbDir);
			var ldb = LdbStringIndex.EnglishPlaintext(rdb);

			var context = new TooltipContext(rdb, ldb, level);
			foreach (var id in ids)
			{
				RenderSpell(context, id, 0, []);
				Console.WriteLine();
			}
		}

		// The same renderer run over the WHOLE catalog, scored rather than printed: how many elements it can turn into
		// a sentence, and how much of a record's bytes the decode accounts for.
		//
		// This is the number #775 was opened on ("46.2% of record bytes inside parsed elements, 13 of 111 property
		// fields read"), made repeatable. A per-spell render says whether ONE tooltip comes out right; only a census
		// says whether the thing generalises, and the two failure modes it separates are the ones that matter - an
		// element kind with no sentence at all (a decode gap) against one whose sentence exists but whose slots are
		// not bound (a binding gap).
		private static void SpellTooltipCensus(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			var ldb = LdbStringIndex.EnglishPlaintext(rdb);
			var ctx = new TooltipContext(rdb, ldb, null);

			long records = 0, bodyBytes = 0, elementBytes = 0, fieldBytes = 0;
			long elements = 0, rendered = 0;
			var fieldCounts = new Dictionary<uint, int>();
			var noSentence = new Dictionary<uint, int>();
			var unbound = new Dictionary<uint, int>();

			foreach (var entry in rdb.EntriesOfType(SpellTemplateType).Where(rdb.CanRead))
			{
				byte[] record;
				try
				{
					record = rdb.ReadDecompressed(entry);
				}
				catch
				{
					continue;
				}

				if (record.Length < 12)
					continue;

				records++;
				var body = record.AsSpan(12);
				bodyBytes += body.Length;

				var fields = RdbSpellImporter.ReadIncreasingFields(body);
				fieldBytes += fields.Count * 8;
				foreach (var field in fields.Keys)
					fieldCounts[field] = fieldCounts.GetValueOrDefault(field) + 1;

				foreach (var element in ItemTemplateStatModifiers.ParseElements(body))
				{
					elements++;
					elementBytes += element.WidthBytes;

					var format = ctx.SentenceFor(element.Kind);
					if (format == null)
					{
						noSentence[element.Kind] = noSentence.GetValueOrDefault(element.Kind) + 1;
						continue;
					}

					var args = BindArgs(ctx, element);
					if (args != null && LdbFormat.TryApply(format, args, out _))
						rendered++;
					else
						unbound[element.Kind] = unbound.GetValueOrDefault(element.Kind) + 1;
				}
			}

			var accounted = elementBytes + fieldBytes;
			Console.WriteLine($"spell-tooltip census over {records} records of RDB {SpellTemplateType}");
			Console.WriteLine($"  record bytes inside a parsed region : {accounted:N0} / {bodyBytes:N0} = {(bodyBytes == 0 ? 0 : 100.0 * accounted / bodyBytes):0.0}%"
			                  + $"   ({elementBytes:N0} in elements, {fieldBytes:N0} in the property run)");
			Console.WriteLine($"  property-run field ids read         : {SpellFieldsRead.Count(f => fieldCounts.ContainsKey(f.Key))} / {fieldCounts.Count}");
			Console.WriteLine($"  elements rendered as a sentence     : {rendered:N0} / {elements:N0} = {(elements == 0 ? 0 : 100.0 * rendered / elements):0.0}%");
			Console.WriteLine();

			Console.WriteLine($"  kinds with NO LDB 170 sentence ({noSentence.Values.Sum():N0} elements over {noSentence.Count} kinds):");
			foreach (var (kind, count) in noSentence.OrderByDescending(k => k.Value).Take(20))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}");

			Console.WriteLine();
			Console.WriteLine($"  kinds WITH a sentence this report cannot fill ({unbound.Values.Sum():N0} elements over {unbound.Count} kinds):");
			foreach (var (kind, count) in unbound.OrderByDescending(k => k.Value).Take(20))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}   \"{ctx.SentenceFor(kind)}\"");

			Console.WriteLine();
			Console.WriteLine("  the property run's unread fields, by how many records carry one:");
			foreach (var (field, count) in fieldCounts.Where(f => !SpellFieldsRead.ContainsKey(f.Key)).OrderByDescending(f => f.Value).Take(20))
				Console.WriteLine($"    field {field,5}  {ctx.StatName(field),-40} {count,7:N0}");
		}

		// `<id>[,<id>...]` plus an optional `@<level>` suffix applying to all of them.
		private static (List<uint> Ids, uint? Level) ParseTooltipArg(string? arg)
		{
			var text = (arg ?? "").Trim();
			uint? level = null;
			var at = text.LastIndexOf('@');
			if (at >= 0)
			{
				if (uint.TryParse(text[(at + 1)..], out var parsed))
					level = parsed;

				text = text[..at];
			}

			var ids = text
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => uint.TryParse(s, out var u) ? u : 0u)
				.Where(u => u != 0)
				.ToList();

			return (ids, level);
		}

		// One spell, whole: its header block, its rendered mechanics, and the accounting line. `seen` is the cycle
		// guard - a chain visiting a spell twice prints the reference and stops rather than recursing.
		private static void RenderSpell(TooltipContext ctx, uint spellId, int depth, HashSet<uint> seen)
		{
			var pad = new string(' ', depth * 2);
			var name = ctx.SpellName(spellId);

			if (!seen.Add(spellId))
			{
				Console.WriteLine($"{pad}{name} ({spellId}) - already shown above");
				return;
			}

			if (!ctx.Rdb.TryGet(SpellTemplateType, spellId, out var entry))
			{
				Console.WriteLine($"{pad}{name} ({spellId}) - NO 1040005 record");
				return;
			}

			byte[] record;
			try
			{
				record = ctx.Rdb.ReadDecompressed(entry);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"{pad}{name} ({spellId}) - unreadable: {ex.Message}");
				return;
			}

			if (record.Length < 12)
			{
				Console.WriteLine($"{pad}{name} ({spellId}) - {record.Length} byte record, no body");
				return;
			}

			var body = record.AsSpan(12);
			var fields = RdbSpellImporter.ReadIncreasingFields(body);
			var elements = ItemTemplateStatModifiers.ParseElements(body);

			Console.WriteLine($"{pad}{name} ({spellId})");
			var description = ctx.Ldb.GetValueOrDefault((LdbStringIndex.SpellDescriptionCategory, spellId));
			if (!string.IsNullOrWhiteSpace(description))
				foreach (var line in description.Replace("\r", "").Split('\n'))
					Console.WriteLine($"{pad}  \"{line}\"");

			// ⚠️ The mana mode is in the record's FLAGS word, not in the property run - three sweeps over the run
			// found nothing before #630 pinned it at word 13 of the whole record, header included.
			var manaIsPercent = record.Length >= ManaFlagsWordOffset + 4 &&
			                    (record.ReadU32Le(ManaFlagsWordOffset) >> ManaCostIsPercentBit & 1) == 1;

			foreach (var line in HeaderLines(fields, elements, manaIsPercent))
				Console.WriteLine($"{pad}  {line}");

			if (elements.Count > 0)
				Console.WriteLine($"{pad}  --");

			foreach (var element in elements)
			{
				foreach (var line in RenderElement(ctx, element))
					Console.WriteLine($"{pad}  {line}");

				// The chain. An element that names another SPELL is the reason a tooltip reads as one sentence and
				// the record does not - see the type comment.
				foreach (var child in ChainedSpells(element))
				{
					if (depth + 1 > MaxChainDepth)
					{
						Console.WriteLine($"{pad}    (chain deeper than {MaxChainDepth}, stopped)");
						continue;
					}

					RenderSpell(ctx, child, depth + 2, seen);
				}
			}

			foreach (var line in AccountingLines(ctx, record, fields, elements))
				Console.WriteLine($"{pad}  {line}");
		}

		// The tooltip's header block - the property-run facts the client draws above the mechanics. Each one names the
		// field it came from, so a wrong line is traceable to a field rather than to "the report".
		private static IEnumerable<string> HeaderLines(Dictionary<uint, uint> fields, IReadOnlyList<ItemTemplateStatModifiers.Element> elements, bool manaIsPercent)
		{
			if (fields.TryGetValue(294, out var castTime))
				yield return $"cast      {castTime / 100.0:0.##} s".TrimEnd() + $"   [field 294 = {castTime} cs]";

			if (fields.TryGetValue(287, out var range))
				yield return $"range     {range / 100.0:0.##}m   [field 287 = {range}]";

			if (fields.TryGetValue(8, out var duration))
				yield return $"duration  {duration / 100.0:0.##} s   [field 8 = {duration} cs]";

			if (fields.TryGetValue(447, out var interval))
				yield return $"ticks     every {interval / 100.0:0.##} s   [field 447 = {interval} cs]";

			// The recast is NOT a property field on most spells - it is a `SetSpellRecastTime` element pointing at the
			// spell itself, which is why #271 could not find it in the run.
			foreach (var element in elements.Where(e => e.Kind == SetSpellRecastTimeKind))
			{
				if (element.Value(GameActionTypes.KeyValue) is { } centiseconds)
					yield return $"recast    {centiseconds / 100.0:0.##} s   [element 53226 SetSpellRecastTime 39={centiseconds}]";
			}

			if (fields.TryGetValue(210, out var rechargeDelay))
				yield return $"recharge  {rechargeDelay} (unit unverified)   [field 210 Recharge Delay, UNREAD by the importer - #633]";

			// ⚠️ The mana cost is usually a PERCENT of the caster's pool (#629/#630), and the pool is a property of
			// the caster, not of this record - so the number the client prints is not reproducible here. Say which
			// mode the record declares and stop.
			if (fields.TryGetValue(132, out var mana))
			{
				yield return manaIsPercent
					? $"mana      {mana / 100.0:0.##}% of the caster's mana pool   [field 132 = {mana}, flags word bit 2 set - the absolute number needs the caster (#629)]"
					: $"mana      {mana / 100.0:0.##}   [field 132 = {mana}, flags word bit 2 clear = absolute]";
			}
		}

		// One element as the client would draw it: its LDB sentence with the slots filled, or - when the slot binding
		// is not established - the unfilled sentence beside the raw key list.
		private static IEnumerable<string> RenderElement(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			var type = element.Type;
			var typeName = type?.Name ?? $"kind {element.Kind}";
			var format = ctx.SentenceFor(element.Kind);
			var args = BindArgs(ctx, element);

			// The element's 0x2d6 is the AURA PERIOD in centiseconds when it is set - a repeating element and a
			// one-shot element are the same kind with the same keys, and the difference is only there (#772).
			var period = element.Fixed2d6 == 0 ? "" : $", every {element.Fixed2d6 / 100.0:0.##} s";

			if (format != null && args != null && LdbFormat.TryApply(format, args, out var text))
			{
				yield return $"{text}{period}   [{typeName}]";
				yield break;
			}

			var raw = type == null
				? string.Join(" ", element.Values.Select((v, i) => $"[{i}]={(int)v}"))
				: string.Join(" ", type.Keys.Select((k, i) => $"{k}={(i < element.Values.Length ? ((int)element.Values[i]).ToString(CultureInfo.InvariantCulture) : "-")}"));

			yield return format == null
				? $"?? {typeName}  {raw}{period}   [no LDB 170 sentence for this type]"
				: $"?? {typeName}  {raw}{period}   [\"{format}\" - slot binding not established]";
		}

		// The arguments the client's own tooltip branch feeds its format string, per kind. Null = this kind's binding
		// is not established, and the caller prints the raw slots instead of inventing one.
		//
		// ⚠️ These are NOT the key list in order. `FilteredAreaFormula`'s keys are [Spell, Radius, MaxTargets, 93,
		// 250] and its sentence takes (effect, radius, radiusUnit, targets, targetsUnit) - the client interleaves each
		// value with the UNIT SUFFIX of the stat it came from, which is the `%s` after every `%.2f`/`%d`.
		private static object?[]? BindArgs(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			var type = element.Type;
			if (type == null)
				return null;

			// A sentence with no conversions in it needs no binding at all - `Stun`, `Root`, `Feared`, `Fatality` and
			// `Inherited weapon attack` are the whole line the client draws. Returning null for those would file a
			// FULLY rendered element as a gap, which is the census reading the wrong way round.
			if (!(ctx.SentenceFor(element.Kind) ?? "").Contains('%'))
				return [];

			switch (element.Kind)
			{
				// `%s %.2fm%s radius up to %d%s targets` / `%s %.2fm%s radius`
				case FilteredAreaFormulaKind:
				case AreaFormulaKind:
				{
					var spell = element.Value(GameActionTypes.KeySpell);
					var radius = element.Value(GameActionTypes.KeyRadius);
					if (spell is null || radius is null)
						return null;

					var effect = $"{ctx.SpellName(spell.Value)} ({spell.Value})";
					if (element.Kind == AreaFormulaKind)
						return [effect, (double)radius.Value, ""];

					var targets = element.Value(KeyAreaMaxTargets);
					return targets is null ? null : [effect, (double)radius.Value, "", (int)targets.Value, ""];
				}

				// `Perform effect %s`
				case CastNanoKind:
				{
					var spell = element.Value(GameActionTypes.KeyValue);
					return spell is null ? null : [$"{ctx.SpellName(spell.Value)} ({spell.Value})"];
				}

				// `%.0f seconds` - centiseconds in the record.
				case SetSpellRecastTimeKind:
				{
					var centiseconds = element.Value(GameActionTypes.KeyValue);
					return centiseconds is null ? null : [centiseconds.Value / 100.0];
				}

				default:
				{
					// The generic modifier shape: a Stat slot plus the magnitude that follows it in the key list. It
					// is the same rule ParseSpellStatEffects uses, so a value this renders is a value the importer
					// would import - which is what makes a screenshot diff a check on the IMPORT and not just on the
					// report.
					var statSlot = type.SlotOf(GameActionTypes.KeyStat);
					if (statSlot < 0 || statSlot + 1 >= type.Keys.Length)
						return null;

					var stat = element.Value(GameActionTypes.KeyStat);
					if (stat is null or 0)
						return null;

					// An interpolated element states a RAMP, not a number: resolve it at the caster level when one was
					// given, and print both endpoints when it was not.
					if (element.Ramp is { } ramp)
					{
						return ctx.Level is { } level
							? [ctx.StatName(stat.Value), ctx.Magnitude(stat.Value, ramp.ValueAt(level))]
							: [ctx.StatName(stat.Value), $"{ctx.Magnitude(stat.Value, ramp.LowValue)} at level {ramp.LowLevel} .. {ctx.Magnitude(stat.Value, ramp.HighValue)} at level {ramp.HighLevel}"];
					}

					var amount = element.Value(type.Keys[statSlot + 1]);
					return amount is null ? null : [ctx.StatName(stat.Value), ctx.Magnitude(stat.Value, (int)amount.Value)];
				}
			}
		}

		// The spells an element hands off to - the links that make a tooltip one sentence.
		private static IEnumerable<uint> ChainedSpells(ItemTemplateStatModifiers.Element element)
		{
			switch (element.Kind)
			{
				case FilteredAreaFormulaKind:
				case AreaFormulaKind:
					if (element.Value(GameActionTypes.KeySpell) is { } areaSpell && areaSpell != 0)
						yield return areaSpell;

					break;

				case CastNanoKind:
					if (element.Value(GameActionTypes.KeyValue) is { } cast && cast != 0)
						yield return cast;

					break;

				// The recast setter names a spell too, and it is always the carrier itself - following it would print
				// the same record under its own header. Deliberately not a chain link.
				case SetSpellRecastTimeKind:
					break;
			}
		}

		// How much of the record the tooltip above actually consumed. The three regions are the fixed head, the
		// ascending property run, and the element list; anything left over is bytes nobody has looked at (#632).
		private static IEnumerable<string> AccountingLines(TooltipContext ctx, byte[] record, Dictionary<uint, uint> fields,
			IReadOnlyList<ItemTemplateStatModifiers.Element> elements)
		{
			var body = record.Length - 12;
			var elementBytes = elements.Sum(e => e.WidthBytes);
			// The run is [fieldId][value] pairs, so its byte span is exactly 8 per field - the reader returns the
			// LONGEST ascending run and a body may hold shorter ones before it, which is why this is a span and not a
			// claim about where the run sits.
			var fieldBytes = fields.Count * 8;
			var accounted = elementBytes + fieldBytes;

			yield return "--";
			yield return $"record {body} B body: {elements.Count} element(s) {elementBytes} B + {fields.Count} property field(s) {fieldBytes} B "
			             + $"= {accounted} B ({(body == 0 ? 0 : 100.0 * accounted / body):0.#}%), {body - accounted} B unaccounted";

			var unparsed = ItemTemplateStatModifiers.UnparsedElementHeads(record.AsSpan(12));
			if (unparsed.Count > 0)
				yield return $"⚠️ {unparsed.Count} element head(s) the scan did not account for: {string.Join(", ", unparsed.Take(8))}";

			// Every property field with the client's own name for it, and whether the importer reads it. This is the
			// #592/#633 backlog rendered for ONE spell, which is the form that says whether a gap matters here.
			foreach (var (field, value) in fields.OrderBy(f => f.Key))
			{
				var read = SpellFieldsRead.TryGetValue(field, out var what);
				var unit = StatUnitSuffix.SuffixOf(field);
				var scale = StatDisplayScale.Of(field);
				var shown = scale == 1 ? value.ToString(CultureInfo.InvariantCulture) : (value / (double)scale).ToString("0.##", CultureInfo.InvariantCulture);
				yield return $"   field {field,5}  {ctx.StatName(field),-38}  {shown}{unit}   {(read ? "read: " + what : "UNREAD")}";
			}
		}

		// The record's FLAGS word (word 13, whole record, header included) and the bit that says how to read the mana
		// cost - see RdbSpellImporter, which owns the decode (#630).
		private const int ManaCostIsPercentBit = 2;
		private const int ManaFlagsWordOffset = 52;

		/// <summary>
		///     What <see cref="RdbSpellImporter" /> actually consumes out of a spell record's property run, field ->
		///     what it becomes. Shared with <c>--report rdb-field-coverage</c>, which is the census this is the
		///     per-spell view of.
		/// </summary>
		/// <remarks>
		///     ⚠️ Hand-maintained, and it has DRIFTED before: it once claimed 79/129/321/551 were read when nothing
		///     read any of them, inflating "read by the importer" from 13 to 17 and hiding four gaps behind a
		///     green-looking column (#633). Keep it in step with the importer's <c>*Field</c> constants. It is one map
		///     rather than two exactly so the drift can only happen once.
		/// </remarks>
		internal static readonly Dictionary<uint, string> SpellFieldsRead = new()
		{
			[8] = "duration", [123] = "rank", [125] = "level", [128] = "spell line (#590)",
			[129] = "spell critical damage (#633)", [132] = "mana cost, mode from the flags word (#629/#630)",
			[147] = "spell critical chance (#633)", [186] = "mount creature (#627)", [287] = "range",
			[294] = "cast time", [347] = "splash radius (#626)", [348] = "splash damage (#626)",
			[349] = "splash max targets", [447] = "channeling interval (#349)", [499] = "mount quality (#627)",
			[847] = "scaling attribute (#592)"
		};

		// The GameAction kinds this renderer binds by hand; the rest go through the generic (Stat, magnitude) rule.
		private const uint AreaFormulaKind = 53087;
		private const uint CastNanoKind = 53051;
		private const uint FilteredAreaFormulaKind = 53088;
		private const uint SetSpellRecastTimeKind = 53226;

		// Everything one rendering needs that is not the record: the RDB, the English LDB, and the caster level a
		// level ramp resolves against.
		private sealed class TooltipContext(RdbReader rdb, Dictionary<(uint Category, uint Id), string> ldb, uint? level)
		{
			public Dictionary<(uint Category, uint Id), string> Ldb { get; } = ldb;
			public uint? Level { get; } = level;
			public RdbReader Rdb { get; } = rdb;

			/// <summary>
			///     The tooltip SENTENCE for a GameAction kind, or null when the client ships none for it.
			/// </summary>
			/// <remarks>
			///     <para>
			///         LDB category 170 is keyed TWO ways, and both are the client's own. About 30 entries are keyed
			///         by the GameAction TYPE ID itself (<c>53088</c> → <c>%s %.2fm%s radius up to %d%s targets</c>);
			///         the rest are keyed by the ELF-28 hash of a TOKEN NAME the tooltip builder pushes -
			///         <c>elf28("IncreaseAOERadius") == 22958195</c>, which is the "Increases the area of effect by %d
			///         meters" line. 21 of the 21 token strings recovered from <c>FUN_0149e450</c>'s 48
			///         category-170 call sites hash onto a shipped id, with no misses
			///         (<c>docs/re/gameaction-tooltip-tokens.tsv</c>).
			///     </para>
			///     <para>
			///         So a kind with no type-id entry is tried under its own NAME, because the client's token names
			///         are the type names - <c>ModifyAsRating</c> (53067) has no id entry and a token entry.
			///         ⚠️ A hash match is evidence and not proof (28 bits, see <see cref="Elf28" />), which is why the
			///         census counts the two key forms separately.
			///     </para>
			/// </remarks>
			public string? SentenceFor(uint kind)
			{
				if (Ldb.TryGetValue((LdbGameActionFormatCategory, kind), out var byId))
					return byId;

				var name = GameActionTypes.Get(kind)?.Name;
				return name != null && Ldb.TryGetValue((LdbGameActionFormatCategory, Elf28.Of(name)), out var byToken)
					? byToken
					: null;
			}

			/// <summary>
			///     The spell's own name, WITHOUT its id - a caller that wants both appends it, and the effect chain
			///     is full of unnamed links where the id is all there is.
			/// </summary>
			public string SpellName(uint spellId)
			{
				return Ldb.TryGetValue((LdbStringIndex.SpellNameCategory, spellId), out var name) && !string.IsNullOrWhiteSpace(name)
					? name
					: $"spell {spellId}";
			}

			public string StatName(uint statId)
			{
				if (Ldb.TryGetValue((LdbStringIndex.StatNameCategory, statId), out var name) && !string.IsNullOrWhiteSpace(name))
					return name;

				return Enum.IsDefined(typeof(CharacterStatId), statId)
					? Enum.GetName(typeof(CharacterStatId), statId)!
					: $"stat {statId}";
			}

			// The client's own value rendering: divide by the stat's display scale, print the sign, append the stat's
			// unit suffix. Trailing zeros are trimmed because the client's float formatter does - the in-client line
			// is "-10%", not "-10.00%".
			public string Magnitude(uint statId, int wireValue)
			{
				var scale = StatDisplayScale.Of(statId);
				var value = wireValue / (double)scale;
				var text = value.ToString("0.##", CultureInfo.InvariantCulture);
				return (value > 0 ? "+" : "") + text + StatUnitSuffix.SuffixOf(statId);
			}
		}
	}

	// The client's LDB format grammar - printf conversions plus two things printf has no notion of.
	//
	//   `#N`        before a conversion, selects ARGUMENT N (1-based) instead of the next one in sequence. The German
	//               and Korean pages reorder their slots, which is what the marker exists for.
	//   `#N{1: a| b}`  a plural/branch form: argument N equal to 1 takes the first arm, anything else the second.
	//   `<font …>`  colour markup the client's rich-text layer eats; stripped here so the output diffs against a
	//               screenshot's TEXT.
	internal static class LdbFormat
	{
		/// <summary>
		///     Fills <paramref name="format" />'s slots from <paramref name="args" />, or fails.
		/// </summary>
		/// <remarks>
		///     ⚠️ It FAILS on a conversion with no argument rather than leaving the `%s` in place. A half-filled
		///     sentence renders as a confident line with a stray `%s` in it, which is exactly the thing this report
		///     must not produce - `Hit`'s `%s %s %s` takes three slots and the generic (stat, magnitude) binder
		///     supplies two, so it has to fall back to the raw key list, not print two thirds of a sentence.
		/// </remarks>
		public static bool TryApply(string format, IReadOnlyList<object?> args, out string text)
		{
			var output = new StringBuilder();
			var next = 0;
			var missing = false;
			for (var i = 0; i < format.Length; i++)
			{
				var c = format[i];

				if (c == '#' && i + 1 < format.Length && char.IsDigit(format[i + 1]))
				{
					var at = i + 1;
					var index = 0;
					while (at < format.Length && char.IsDigit(format[at]))
						index = index * 10 + (format[at++] - '0');

					// `#N{...}` is the branch form; `#N%…` merely re-points the next conversion.
					if (at < format.Length && format[at] == '{')
					{
						var close = format.IndexOf('}', at);
						if (close < 0)
						{
							output.Append(format, i, format.Length - i);
							break;
						}

						output.Append(Branch(format[(at + 1)..close], Arg(args, index - 1)));
						i = close;
						continue;
					}

					next = index - 1;
					i = at - 1;
					continue;
				}

				if (c != '%')
				{
					output.Append(c);
					continue;
				}

				if (i + 1 < format.Length && format[i + 1] == '%')
				{
					output.Append('%');
					i++;
					continue;
				}

				// A conversion: [flags/width/precision][conversion char]. Only the precision matters for the output.
				var end = i + 1;
				while (end < format.Length && !"sdfiu".Contains(format[end]))
					end++;

				if (end >= format.Length)
				{
					output.Append(format, i, format.Length - i);
					break;
				}

				var spec = format[(i + 1)..end];
				var conversion = format[end];
				var value = Arg(args, next);
				if (value == null)
					missing = true;

				output.Append(Convert(conversion, spec, value));
				next++;
				i = end;
			}

			text = StripMarkup(output.ToString());
			return !missing;
		}

		private static object? Arg(IReadOnlyList<object?> args, int index)
		{
			return index >= 0 && index < args.Count ? args[index] : null;
		}

		// `1: singular| plural` - the leading number is the value the first arm is chosen for.
		private static string Branch(string body, object? value)
		{
			var colon = body.IndexOf(':');
			var arms = (colon >= 0 ? body[(colon + 1)..] : body).Split('|');
			if (arms.Length == 0)
				return "";

			// ⚠️ The selector is a NUMBER. A branch arm fed the wrong slot - a stat name where the client passes an
			// increase/decrease flag - is a binding that does not hold, so it takes the plural arm rather than
			// throwing on a parse: `ModifyAsRating`'s `#1{1:increases | decreases} #2%s by #3%d` wants three
			// arguments and the generic (stat, magnitude) binder supplies two.
			var match = colon >= 0 && int.TryParse(body[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wanted)
			            && value is not string
			            && value is IConvertible convertible
			            && System.Convert.ToDouble(convertible, CultureInfo.InvariantCulture) == wanted;

			return (match ? arms[0] : arms[^1]).Trim();
		}

		private static string Convert(char conversion, string spec, object? value)
		{
			if (value == null)
				return "%" + spec + conversion;

			if (conversion == 's')
				return value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

			var number = value is IConvertible convertible ? System.Convert.ToDouble(convertible, CultureInfo.InvariantCulture) : 0;
			if (conversion is 'd' or 'i' or 'u')
				return ((long)number).ToString(CultureInfo.InvariantCulture);

			// `%.2f` - the precision is what the client uses to draw a radius as `7.00m`.
			var dot = spec.IndexOf('.');
			var decimals = dot >= 0 && int.TryParse(spec[(dot + 1)..], out var d) ? d : 6;
			return number.ToString("F" + decimals, CultureInfo.InvariantCulture);
		}

		// The rich-text markup the client's layout layer consumes rather than draws.
		private static string StripMarkup(string text)
		{
			var output = new StringBuilder(text.Length);
			var depth = 0;
			foreach (var c in text)
			{
				if (c == '<')
				{
					depth++;
					continue;
				}

				if (c == '>')
				{
					if (depth > 0)
						depth--;

					continue;
				}

				if (depth == 0)
					output.Append(c);
			}

			return output.ToString();
		}
	}
}

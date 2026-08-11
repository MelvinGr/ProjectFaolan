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

			long records = 0, bodyBytes = 0, elementBytes = 0, fieldBytes = 0, listBytes = 0, stringBytes = 0, withText = 0, listFieldBytes = 0, withListFields = 0;
			long elements = 0, rendered = 0;
			// #789: how much of the catalog carries the client's hide-from-tooltip bit, and a sample of what it
			// selects - the only way to say what the flag MEANS is to look at which spells wear it.
			long hidden = 0, hiddenRanked = 0, shownRanked = 0, hiddenNameless = 0, shownNameless = 0, shown = 0;
			var hiddenSample = new List<string>();
			var fieldCounts = new Dictionary<uint, int>();
			var noSentence = new Dictionary<uint, int>();
			// The kinds whose lack of a 170 sentence is PROVEN rather than merely unfound - see
			// GameActionTooltip.NoSentenceReason.
			var noSentenceByConstruction = new Dictionary<uint, int>();
			var notDrawn = new Dictionary<uint, int>();
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

				var fields = RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out var afterRun);

				var spellName = ctx.SpellName(entry.Id);
				var ranked = spellName.Contains("(Rank ", StringComparison.Ordinal);
				var nameless = spellName.StartsWith("spell ", StringComparison.Ordinal);
				if ((RdbSpellImporter.ReadFlagsWord(body, runStart) >> HiddenFromTooltipBit & 1) == 1)
				{
					hidden++;
					if (ranked)
						hiddenRanked++;

					if (nameless)
						hiddenNameless++;

					if (hiddenSample.Count < 12)
						hiddenSample.Add($"{spellName} ({entry.Id})");
				}
				else
				{
					shown++;
					if (ranked)
						shownRanked++;

					if (nameless)
						shownNameless++;
				}

				// ⭐ #632: the whole PAIR REGION is decoded now, not just the property run - the 20-byte prefix, the
				// head's own pairs, the run, and any trailing resource reference. Its length is exactly where the
				// string block starts, which the record itself states in its frame word.
				fieldBytes += afterRun;
				// #632: the record's own name + description sit immediately after the run, and were a large slice of
				// what that ticket measured as "never looked at".
				var block = RdbSpellImporter.ReadStringBlock(body, afterRun);
				stringBytes += block.Bytes;
				if (block.Bytes > 0)
					withText++;

				// #632: the two LIST-valued fields (22 and 36) that follow the text ones on 1,871 records.
				var listFields = ScanListFields(body, afterRun + block.Bytes);
				listFieldBytes += listFields;
				if (listFields > 0)
					withListFields++;
				foreach (var field in fields.Keys)
					fieldCounts[field] = fieldCounts.GetValueOrDefault(field) + 1;

				// The element-list headers (#785) - 12 bytes each, and decoded, so they count as accounted for.
				listBytes += ItemTemplateStatModifiers.ParseElementLists(body).Count * 12;

				foreach (var element in ItemTemplateStatModifiers.ParseElements(body))
				{
					elements++;
					elementBytes += element.WidthBytes;

					var (format, args) = Sentence(ctx, element);
					if (format == null)
					{
						// ⭐ Split THREE ways, because only one of them is OURS. A kind the tooltip builder has no
						// branch for falls to its default and the client draws nothing either; a kind whose absence
						// is PROVEN (a category-100 label, or a number that lives in the invocation context) is
						// settled rather than outstanding. Counting either as a missing sentence reads as decode debt
						// when it is retail's own behaviour.
						var bucket = !GameActionTooltip.HasTooltipBranch(element.Kind) ? notDrawn
							: GameActionTooltip.NoSentenceReason(element.Kind) != null ? noSentenceByConstruction
							: noSentence;
						bucket[element.Kind] = bucket.GetValueOrDefault(element.Kind) + 1;
						continue;
					}

					if (args != null && LdbFormat.TryApply(format, args, out _))
						rendered++;
					else
						unbound[element.Kind] = unbound.GetValueOrDefault(element.Kind) + 1;
				}
			}

			var accounted = elementBytes + fieldBytes + listBytes + stringBytes + listFieldBytes;
			Console.WriteLine($"spell-tooltip census over {records} records of RDB {SpellTemplateType}");
			Console.WriteLine($"  record bytes inside a parsed region : {accounted:N0} / {bodyBytes:N0} = {(bodyBytes == 0 ? 0 : 100.0 * accounted / bodyBytes):0.0}%"
			                  + $"   ({elementBytes:N0} in elements, {listBytes:N0} in their list headers, {fieldBytes:N0} in the head + property run,"
			                  + $" {stringBytes:N0} in the name/description block on {withText:N0} records,"
			                  + $" {listFieldBytes:N0} in the list-valued fields on {withListFields:N0})");
			Console.WriteLine($"  property-run field ids read         : {SpellFieldsRead.Count(f => fieldCounts.ContainsKey(f.Key))} / {fieldCounts.Count}");
			Console.WriteLine($"  elements rendered as a sentence     : {rendered:N0} / {elements:N0} = {(elements == 0 ? 0 : 100.0 * rendered / elements):0.0}%");
			// The honest headline: an element the CLIENT draws nothing for is accounted for too, so the outstanding
			// work is what is left after both.
			var accountedElements = rendered + notDrawn.Values.Sum() + noSentenceByConstruction.Values.Sum();
			Console.WriteLine($"  ...or accounted for                 : {accountedElements:N0} / {elements:N0} = {(elements == 0 ? 0 : 100.0 * accountedElements / elements):0.0}%"
			                  + "   (rendered, or a kind whose blank line is the client's own behaviour)");
			Console.WriteLine();

			Console.WriteLine($"  kinds the CLIENT does not draw either - no branch in FUN_0149e450, so they reach its default"
			                  + $" ({notDrawn.Values.Sum():N0} elements over {notDrawn.Count} kinds):");
			foreach (var (kind, count) in notDrawn.OrderByDescending(k => k.Value).Take(12))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}");

			Console.WriteLine();
			Console.WriteLine($"  kinds with a branch that PROVABLY has no 170 sentence - settled, not outstanding"
			                  + $" ({noSentenceByConstruction.Values.Sum():N0} elements over {noSentenceByConstruction.Count} kinds):");
			foreach (var (kind, count) in noSentenceByConstruction.OrderByDescending(k => k.Value))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}   {GameActionTooltip.NoSentenceReason(kind)}");

			Console.WriteLine();
			Console.WriteLine($"  kinds WITH a branch but no sentence we can find ({noSentence.Values.Sum():N0} elements over {noSentence.Count} kinds):");
			foreach (var (kind, count) in noSentence.OrderByDescending(k => k.Value).Take(20))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}");

			Console.WriteLine();
			Console.WriteLine($"  kinds WITH a sentence this report cannot fill ({unbound.Values.Sum():N0} elements over {unbound.Count} kinds):");
			foreach (var (kind, count) in unbound.OrderByDescending(k => k.Value).Take(20))
				Console.WriteLine($"    {kind}  {GameActionTypes.Get(kind)?.Name ?? "?",-40} {count,7:N0}   \"{ctx.SentenceFor(kind)}\"");

			Console.WriteLine();
			Console.WriteLine($"  spells carrying the hide-from-tooltip bit (flags bit {HiddenFromTooltipBit}, #789): {hidden:N0} / {records:N0}"
			                  + $" = {(records == 0 ? 0 : 100.0 * hidden / records):0.0}%");
			Console.WriteLine($"    named \"(Rank N)\"       : {hiddenRanked,6:N0} / {hidden:N0} flagged = {(hidden == 0 ? 0 : 100.0 * hiddenRanked / hidden):0.0}%"
			                  + $"   against {shownRanked:N0} / {shown:N0} = {(shown == 0 ? 0 : 100.0 * shownRanked / shown):0.0}% of the rest");
			Console.WriteLine($"    NO name in LDB 50210 : {hiddenNameless,6:N0} / {hidden:N0} flagged = {(hidden == 0 ? 0 : 100.0 * hiddenNameless / hidden):0.0}%"
			                  + $"   against {shownNameless:N0} / {shown:N0} = {(shown == 0 ? 0 : 100.0 * shownNameless / shown):0.0}% of the rest");
			foreach (var sample in hiddenSample)
				Console.WriteLine($"    {sample}");

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
			// ⚠️ ONE scan. `ReadIncreasingFields` is quadratic in the record's word count (it restarts the ascending
			// walk at every offset), so the run end is taken from this call and threaded down rather than re-derived
			// where it is needed.
			var fields = RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out var runEnd);
			var elements = ItemTemplateStatModifiers.ParseElements(body);

			// ⭐ #632: the record carries its OWN name and description, and for the 1,594 spells LDB 50210 has no name
			// for it is the only one there is - `Hellfire Stream` (8600) renders as "spell 8600" out of the LDB. The
			// LDB still wins where it has one: it is the LOCALISED string and this is the raw authoring name.
			var inline = RdbSpellImporter.ReadStringBlock(body, runEnd);
			if (name.StartsWith("spell ", StringComparison.Ordinal) && inline.Name.Length > 0)
				name = $"{inline.Name} [from the record]";

			Console.WriteLine($"{pad}{name} ({spellId})");
			var description = ctx.Ldb.GetValueOrDefault((LdbStringIndex.SpellDescriptionCategory, spellId));
			if (string.IsNullOrWhiteSpace(description) && inline.Description.Length > 0)
				description = inline.Description;
			if (!string.IsNullOrWhiteSpace(description))
				foreach (var line in description.Replace("\r", "").Split('\n'))
					Console.WriteLine($"{pad}  \"{line}\"");

			// ⚠️ The mana mode is in the record's FLAGS word, not in the property run - three sweeps over the run
			// found nothing before #630 pinned it at word 13 of the whole record, header included.
			var manaIsPercent = (RdbSpellImporter.ReadFlagsWord(body, runStart) >> ManaCostIsPercentBit & 1) == 1;

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

			foreach (var line in AccountingLines(ctx, record, fields, elements, inline.Bytes, runEnd))
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
			// The byte OFFSET rides with the name, because "which element" is the first question asked of a line that
			// looks wrong - and a record that states the same element twice (#783) is otherwise two identical lines
			// with nothing to tell them apart.
			var typeName = (type?.Name ?? $"kind {element.Kind}") + $"@{element.Offset}";
			var (format, args) = Sentence(ctx, element);

			// The element's 0x2d6 is the AURA PERIOD in centiseconds when it is set - a repeating element and a
			// one-shot element are the same kind with the same keys, and the difference is only there (#772).
			var period = element.Fixed2d6 == 0 ? "" : $", every {element.Fixed2d6 / 100.0:0.##} s";

			// #789: the client checks a HIDE bit on a spell an element references, and draws nothing for the element
			// when it is set. Flagged rather than acted on - see HiddenSpellNote.
			var hidden = HiddenSpellNote(ctx, element);

			if (format != null && args != null && LdbFormat.TryApply(format, args, out var text))
			{
				yield return $"{text}{period}   [{typeName}]{hidden}";
				yield break;
			}

			var raw = type == null
				? string.Join(" ", element.Values.Select((v, i) => $"[{i}]={(int)v}"))
				: string.Join(" ", type.Keys.Select((k, i) => $"{k}={(i < element.Values.Length ? ((int)element.Values[i]).ToString(CultureInfo.InvariantCulture) : "-")}"));

			yield return format == null
				? $"?? {typeName}  {raw}{period}   [no LDB 170 sentence for this type]{hidden}"
				: $"?? {typeName}  {raw}{period}   [\"{format}\" - slot binding not established]{hidden}";
		}

		/// <summary>
		///     The byte length of the record's LIST-valued fields (22 and 36), searched for from <paramref name="from" />.
		/// </summary>
		/// <remarks>
		///     ⚠️ Searched rather than read at a fixed place: the block does not always sit immediately after the text
		///     - on `Spirit of the Bear (Rank 1)` it appears between two element lists - so its POSITION is not part of
		///     the grammar yet even though its shape is (#632).
		/// </remarks>
		private static int ScanListFields(ReadOnlySpan<byte> body, int from)
		{
			for (var o = Math.Max(0, from); o + 8 <= body.Length; o += 4)
			{
				var length = RdbSpellImporter.ReadListFieldsLength(body, o);
				if (length > 0)
					return length;
			}

			return 0;
		}

		/// <summary>
		///     A marker for an element whose referenced spell carries the client's hide-from-tooltip bit (#789), or the
		///     empty string.
		/// </summary>
		/// <remarks>
		///     ⚠️ Deliberately a NOTE and not a suppression. `FUN_014a3b30` makes `ConeFormula` skip its whole sentence
		///     and makes `AddComboProc` treat that side as absent, so acting on it would make lines DISAPPEAR - and
		///     only bits 1-2 of the flags word this bit lives in are proven to reach the client verbatim (#629/#630).
		///     Over-drawing is visible in a screenshot diff; under-drawing is not, so the report takes the visible
		///     failure. Flip this to a suppression once the bit is confirmed in-client.
		/// </remarks>
		private static string HiddenSpellNote(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			if (element.Kind is not (ConeFormulaKind or AddComboProcKind))
				return "";

			var flagged = new[] { GameActionTypes.KeySpell, GameActionTypes.KeyProcTargetSpell }
				.Select(element.Value)
				.Where(v => v is not (null or 0))
				.Where(v => ctx.SpellHiddenFromTooltip(v!.Value))
				.Select(v => v!.Value)
				.ToList();

			return flagged.Count == 0
				? ""
				: $"   ⚠️ retail draws nothing here - {string.Join("/", flagged)} carries the hide-from-tooltip bit (#789)";
		}

		/// <summary>
		///     The sentence the client draws this element with, and the arguments it feeds it. A null format means the
		///     client ships none; null args mean this kind's slot binding is not established.
		/// </summary>
		/// <remarks>
		///     Kept as ONE function because the two halves are not independent: the <c>Hit</c> family's sentence is
		///     CHOSEN BY ITS VALUES (health vs another stat, damage vs heal - see
		///     <see cref="GameActionTooltip.HitSentenceToken" />), so "which string" cannot be answered before the
		///     slots are read. Splitting them is what would force a guess (#782).
		/// </remarks>
		private static (string? Format, object?[]? Args) Sentence(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			// The damage/heal family - `Hit`, `SpecialHit` and `InterpolatedHit`, all four keys of the first two being
			// (Stat, min, max, flag). The `%s %s %s` sitting at their own type id is NOT what the client draws; the
			// three token sentences below are (branch 0x014a0ae6 -> FUN_0149aa30).
			if (element.Kind is HitKind or SpecialHitKind or InterpolatedHitKind)
				return HitSentence(ctx, element);

			// The hit-percent pair, whose sentence is likewise chosen by the SIGN of its magnitude. The
			// `Change hit percent for %d by %d%%` sitting at 53030's own id is not what the client draws either
			// (branch 0x014a018c): it reads key 39, picks `IncreasesHitPercent` when positive and
			// `DecreasesHitPercent` otherwise, and renders the value x0.01 with a `%` - hundredths of a percent, the
			// same unit `Amount` carries elsewhere.
			if (element.Kind is ChangeHitPercentKind or ChangeLineHitPercentKind)
			{
				if (element.Value(GameActionTypes.KeyValue) is not { } raw)
					return (null, null);

				var percent = (int)raw;
				var token = percent > 0 ? "IncreasesHitPercent" : "DecreasesHitPercent";
				var format = ctx.SentenceForToken(token);
				return (format, format == null ? null : [$"{percent * 0.01:0.##}%"]);
			}

			// The PERCENT-of-a-stat family, whose sentence is chosen by three things at once - which stat, the sign of
			// the percentage, and a mode word (branch 0x014a0b99).
			if (element.Kind == HitPercentageKind)
				return HitPercentageSentence(ctx, element);

			// `AddPercentSkillModifier` (branch 0x014a1b52) - one of the kinds #782 named as having no Stat slot at
			// all, so the generic (Stat, magnitude) binder cannot touch it and it sat in the "no sentence we can
			// find" bucket. It has two, and the branch picks between them by COMPARING ITS TWO STAT SLOTS:
			//
			//     CMP ESI,EDI ; JNZ <different-stat arm>      ESI = GetIntStat(0x315) = key 66
			//                                                 EDI = GetIntStat(0x316) = key 67
			//                                                 EBX = GetIntStat(0x2f9) = key 39, the percentage
			//
			//   same      `#2{1: Increases | Decreases } your #3%s by #1%s`
			//   different `#2{1: Increases | Decreases } your #4%s by #1%s of your #3%s`
			//
			// ⭐ The SLOT ORDER is read off the binds rather than guessed: both arms append the value, then the
			// `SETG` of the value's sign as the branch selector, then the stat saved at `[ESP+0x10]` (key 66), and
			// the different-stat arm appends one more from `[ESP+0x24]` (key 67). So `#3` is key 66 and `#4` is key
			// 67 - "increases your <67> by N% of your <66>", i.e. 66 is the SOURCE stat and 67 the one raised.
			//
			// The shipped data agrees independently: `Absolute Philtre of Constitution` carries 66=806, 67=806
			// (equal -> the same-stat arm) with 39=8, and `Arcane Plunder` carries 66=804 Strength, 67=490, 39=2..8,
			// which reads as "increases your 490 by 2% of your Strength" and not the other way round.
			//
			// ⚠️ The magnitude is a PLAIN percent here (8, 13, 2..8), not the hundredths `ChangeHitPercent` and
			// `UpgradeCombo` carry - so it is rendered `8%`, not `0.08%`. The sentence has no literal `%`, so the
			// suffix belongs to the value string, which is what the branch's own value formatter appends.
			if (element.Kind == AddPercentSkillModifierKind)
			{
				var sourceStat = element.Value(SkillModifierSourceKey);
				var targetStat = element.Value(SkillModifierTargetKey);
				var percent = element.Value(GameActionTypes.KeyValue);
				if (sourceStat is null or 0 || targetStat is null or 0 || percent is null)
					return (null, null);

				var signed = (int)percent.Value;
				var same = sourceStat.Value == targetStat.Value;
				var format = ctx.SentenceForToken(same ? "AddPercentSkillModifierSameStat" : "AddPercentSkillModifierDifferentStat");
				if (format == null)
					return (null, null);

				object?[] args = same
					? [$"{signed}%", signed > 0 ? 1 : 0, ctx.StatName(sourceStat.Value)]
					: [$"{signed}%", signed > 0 ? 1 : 0, ctx.StatName(sourceStat.Value), ctx.StatName(targetStat.Value)];

				return (format, args);
			}

			// `ColumnAddTargets` picks between its own type-id sentence and a token one on whether it carries a
			// DISTANCE (branch 0x014a08f0): it takes |key 238| and compares it against the 0.005 at 0x01f27378,
			// falling to `%.2fm%s wide column to target` when there is none and to `ColumnAddTargetsWithDistance`
			// when there is, which appends the distance and its own modifier annotation.
			//
			// ⚠️ Its two magnitudes do NOT come from the `GetIntStat(GameActionStat_e)` every other branch uses - it
			// reads them through `FUN_014af090(record, <statKey>)`, which takes the key as a PUSHED argument and
			// returns a FLOAT. That is why a dump anchored on `MOV ECX,<key>; CALL 0x0077a510` reported this branch
			// as reading no keys at all (#782).
			//
			// ⚠️⚠️ And that getter ends `CVTSI2SS; MULSS XMM0,[0x01F2800C]` - it divides the stored integer by 100
			// on the way out, where the int getter every other branch uses hands the value back verbatim. So a stored
			// 50 is a HALF-METRE column, not a 50m one; the client's own `%.2f` is what makes a 100x error here look
			// like a finished line rather than a failed bind.
			if (element.Kind == ColumnAddTargetsKind)
			{
				var columnWidth = element.Value(GameActionTypes.KeyRadius);
				var columnDistance = element.Value(KeyColumnDistance);
				if (columnWidth is null || columnDistance is null)
					return (null, null);

				if (columnDistance.Value == 0)
					return (ctx.SentenceFor(element.Kind), [(int)columnWidth.Value * ColumnScale, ""]);

				var withDistance = ctx.SentenceForToken("ColumnAddTargetsWithDistance");
				return (withDistance, withDistance == null
					? null
					: [(int)columnWidth.Value * ColumnScale, "", (int)columnDistance.Value * ColumnScale, ""]);
			}

			// `AddComboProc` picks its sentence by WHICH of its two spell slots are present (branch 0x0149ec8e): key
			// 88 is the proc run on SELF and key 236 the one run on the TARGET, and the branch has a token for each
			// of the three combinations - `%s on self and %s on target`, `%s on self`, `%s on target`. The
			// `Add Combo Proc. Run #1%s on self and #2%s on target. Triggers #4%d times per minute.` sitting at its
			// own type id is not what the client draws.
			//
			// ⚠️ The branch reads key 237 (procs per minute) and appends it as a third argument that none of the
			// three token sentences has a slot for, so it does not appear - the same shape as `Knockback`'s key 191.
			if (element.Kind == AddComboProcKind)
			{
				var onSelf = element.Value(GameActionTypes.KeySpell) ?? 0;
				var onTarget = element.Value(GameActionTypes.KeyProcTargetSpell) ?? 0;

				// ⚠️ The client's presence test is `id != 0 && !hidden`, and a side whose spell carries the
				// hide-from-tooltip bit renders as a ONE-sided proc. Not acted on here for the same reason as the
				// cone kinds - see #789 and HiddenSpellNote; the line is drawn and flagged.
				var self = onSelf != 0 ? $"{ctx.SpellName(onSelf)} ({onSelf})" : null;
				var target = onTarget != 0 ? $"{ctx.SpellName(onTarget)} ({onTarget})" : null;

				var (procToken, procArgs) = (self, target) switch
				{
					(not null, not null) => ("AddComboProcBoth", (object?[])[self, target]),
					(not null, null) => ("AddComboProcSelf", [self]),
					(null, not null) => ("AddComboProcOther", [target]),
					_ => (null, null)
				};

				if (procToken == null)
					return (null, null);

				var procFormat = ctx.SentenceForToken(procToken);
				return (procFormat, procFormat == null ? null : procArgs);
			}

			return (ctx.SentenceFor(element.Kind), BindArgs(ctx, element));
		}

		// `HitPercentage` (53007), keys [Stat, Value, Line, 71]. Its own type id carries `%s %s by %d%% of %s %s`, and
		// the client draws none of it: branch 0x014a0b99 reads all four slots plus key 32, and picks one of five token
		// sentences.
		//
		// ⚠️ The MAGNITUDE is raw here. The client builds the value string with `FUN_0149a700(v, v, 0)` - the same
		// range builder the `Hit` family uses, but with the divide flag passed as a literal 0, where `Hit` passes the
		// stat's own percent predicate. So a `HitPercentage` value is NOT divided by the stat's display scale, and
		// scaling it would report a 30% drain as 0.3%.
		private static (string? Format, object?[]? Args) HitPercentageSentence(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			var stat = element.Value(GameActionTypes.KeyStat);
			var value = element.Value(GameActionTypes.KeyValue);
			var otherStat = element.Value(GameActionTypes.KeyLine);
			if (stat is null or 0 || value is null || otherStat is null)
				return (null, null);

			// ⛔ Mode 1 or 2 takes a different path entirely: the client reads the CHARACTER'S OWN value of the key-1
			// stat and renders `charStat * value * 0.01` through the damage/heal renderer. That number is a property
			// of the caster, not of the record, so this report cannot produce it.
			// The SENTENCE is still determinable (the computed value keeps the stored one's sign, and the renderer is
			// the `Hit` family's), so this reports as "cannot fill" rather than "no sentence" - the number is what is
			// missing, not the string.
			if (element.Value(KeyHitPercentMode) is 1 or 2)
				return (ctx.SentenceForToken(GameActionTooltip.HitSentenceToken(stat.Value, (int)value.Value)), null);

			var signed = (int)value.Value;
			var range = ctx.RawRange(signed, signed);

			if (stat == GameActionTooltip.HealthStatId)
			{
				// ⛔ The DAMAGE arm's second slot is a damage SCHOOL name, resolved by `FUN_0149a8f0` through an
				// enum-name table keyed on key 71 (values 1..14) - the resolver is not decoded, so binding this arm
				// would leave a `%s` in the middle of a finished-looking line.
				if (signed <= 0)
					return (ctx.SentenceForToken("HitPercentDamage"), null);

				var heal = ctx.SentenceForToken("HitPercentHeal");
				return (heal, heal == null ? null : [range]);
			}

			// Stamina, drained: the second slot is simply that stat's own NAME.
			// ⚠️ The client appends its `OnTarget` token to this one; that qualifier is not reproduced here.
			if (stat == StaminaStatId && signed < 0)
			{
				var drain = ctx.SentenceForToken("HitPercentDamage");
				return (drain, drain == null ? null : [range, ctx.StatName(stat.Value)]);
			}

			// Everything else is the three-slot form: how much, of WHICH stat, based on WHICH other stat.
			var token = signed > 0 ? "HitPercentHealStatByOtherStat" : "HitPercentDamageStatByOtherStat";
			var format = ctx.SentenceForToken(token);
			return (format, format == null ? null : [range, ctx.StatName(stat.Value), ctx.StatName(otherStat.Value)]);
		}

		// `Hit` / `SpecialHit` carry their two magnitudes flat; `InterpolatedHit` ramps each of them over the caster's
		// level, which is the same four-slot ramp shape the interpolated modifiers use, doubled: (168 @172 -> 169 @173)
		// is the LOW magnitude and (170 @172 -> 171 @173) the high one. Gangrenous Stench's tick states -5840..-7140 at
		// level 75 and -6220..-7610 at 80, which is what pins which pair is which.
		private static (string? Format, object?[]? Args) HitSentence(TooltipContext ctx, ItemTemplateStatModifiers.Element element)
		{
			var stat = element.Value(GameActionTypes.KeyStat);
			if (stat is null or 0)
				return (null, null);

			int min, max;
			if (element.Kind == InterpolatedHitKind)
			{
				var lowLevel = element.Value(KeyRampLowLevel);
				var highLevel = element.Value(KeyRampHighLevel);
				var minLow = element.Value(KeyHitMinLow);
				var minHigh = element.Value(KeyHitMinHigh);
				var maxLow = element.Value(KeyHitMaxLow);
				var maxHigh = element.Value(KeyHitMaxHigh);
				if (lowLevel is null || highLevel is null || minLow is null || minHigh is null || maxLow is null || maxHigh is null)
					return (null, null);

				// Without a caster level there is no single number to draw, so the ramp prints as its two ENDPOINTS
				// rather than being resolved at an invented level.
				if (ctx.Level is not { } level)
				{
					var endpoints = $"{ctx.Range(stat.Value, (int)minLow.Value, (int)maxLow.Value)} at level {lowLevel} .. "
					                + $"{ctx.Range(stat.Value, (int)minHigh.Value, (int)maxHigh.Value)} at level {highLevel}";
					return HitFormat(ctx, stat.Value, (int)minLow.Value, [endpoints, ctx.StatName(stat.Value)]);
				}

				min = new LevelRamp((int)minLow.Value, (int)minHigh.Value, lowLevel.Value, highLevel.Value).ValueAt(level);
				max = new LevelRamp((int)maxLow.Value, (int)maxHigh.Value, lowLevel.Value, highLevel.Value).ValueAt(level);
			}
			else
			{
				var minValue = element.Value(KeyHitMin);
				var maxValue = element.Value(KeyHitMax);
				if (minValue is null || maxValue is null)
					return (null, null);

				min = (int)minValue.Value;
				max = (int)maxValue.Value;
			}

			return HitFormat(ctx, stat.Value, min, [ctx.Range(stat.Value, min, max), ctx.StatName(stat.Value)]);
		}

		// The three token sentences and their argument shape. `Heals %s` takes the range alone; the other two take the
		// range and the stat.
		private static (string? Format, object?[]? Args) HitFormat(TooltipContext ctx, uint statId, int min, object?[] args)
		{
			var token = GameActionTooltip.HitSentenceToken(statId, min);
			var format = ctx.SentenceForToken(token);
			return (format, format == null ? null : args);
		}

		// The arguments the client's own tooltip branch feeds its format string, per kind. Null = this kind's binding
		// is not established, and the caller prints the raw slots instead of inventing one.
		//
		// ⚠️ These are NOT the key list in order. `FilteredAreaFormula`'s keys are [Spell, Radius, MaxTargets, 93,
		// 250] and its sentence takes five slots where the record has three magnitudes.
		//
		// ⭐ The extra `%s` after every `%.2f`/`%d` is the VALUE-MODIFIER ANNOTATION, not a unit (the unit is in the
		// literal text - `%.2fm%s radius`). Every magnitude the builder appends is appended twice: the resolved
		// number, then `FUN_014a3880(resolved - raw, <unit token>)`, which is `FUN_014aa410` - it returns the EMPTY
		// string when the difference is zero and otherwise
		// `<font color="tooltip_positive|negative">` + LDB170(`ValueModSign{Positive,Negative}`) + the delta + the
		// unit. So a tooltip drawn with no character modifier in play has an empty string in every one of those
		// slots, which is what `""` means at each of them below (#782).
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

				// The CONE kinds: `%s %.2f%s degree arc, %.2fm%s radius` (53259, which names the effect spell) and
				// the same line without the leading `%s` (53273, which does not). Both branches - 0x014a2316 and
				// 0x014a28f9 - append exactly (arc, arcMod, radius, radiusMod), and 53259 prefixes the spell link;
				// the append COUNT matching each sentence's slot count is the structural check that the two are
				// paired the right way round.
				//
				// ⭐ The ARC is key 213 and the RADIUS is key 11, which is the opposite of the order the branch
				// FETCHES them in (it reads 11 first). Two independent pins: the radius slot's value is what
				// `SpellAreaEffects.RadiusMetres` holds (6 for `Wave of Life (Rank 2)`, whose key 11 is 6), and the
				// arc slot's value is clamped to [0, 360] against the 360.0 at 0x020b5270. Key 212 (the cone's
				// negative half-angle, always -213/2) is not read by either branch.
				//
				// Both magnitudes go through the client's value-modifier helpers before being drawn, and both reduce
				// to the raw key here: the MULTIPLICATIVE term is `(stat 0x57B * 0.1 * 0.01 * w + 1) * value`
				// (`FUN_014a9f90`) where `w` is a per-call float global - 0x02D25E34 and 0x02D25E54 for these two -
				// and all of those globals are 0.0 in the image with no writer anywhere in it, so the term is exactly
				// 1; the ADDITIVE term (`FUN_014a9c10`) comes off the viewing CHARACTER and is 0 without one. What a
				// character does contribute shows up in the `%s` annotation, not in the number. See #782.
				case ConeFormulaKind:
				case AttackAddTargetsKind:
				{
					var arc = element.Value(KeyConeArcDegrees);
					var coneRadius = element.Value(GameActionTypes.KeyRadius);
					if (arc is null || coneRadius is null)
						return null;

					object?[] cone = [(double)(int)arc.Value, "", (double)(int)coneRadius.Value, ""];
					if (element.Kind == AttackAddTargetsKind)
						return cone;

					// ⚠️ 53259 checks the hide-from-tooltip bit on its effect spell BEFORE building anything and
					// skips its whole sentence when it is set (#789). This renderer does NOT act on that: the bit
					// is bit 24 of the flags word and only bits 1-2 of that word are proven to reach the client
					// verbatim, so suppressing on it would silently DROP lines if the reading is wrong - the worse
					// direction of the two. The line is drawn and flagged instead (see HiddenSpellNote).
					var coneSpell = element.Value(GameActionTypes.KeySpell);
					return coneSpell is null
						? null
						: [$"{ctx.SpellName(coneSpell.Value)} ({coneSpell.Value})", .. cone];
				}

				// `%s resistance to %s` - the value, then the RESISTANCE KIND. Branch 0x0149e942 reads key 152 and
				// key 39, renders the value through stat 0x4B `MetaType`'s display scale (which is 1, so it prints
				// raw), and resolves key 152 through LDB category 55200 - the client's spell-effect/metatype catalog
				// (`68` is "Poison/Electrical resistance rating buff"), NOT the stat-name category.
				case NanoResistanceKind:
				{
					var metaType = element.Value(KeyNanoResistanceMetaType);
					var amount = element.Value(GameActionTypes.KeyValue);
					if (metaType is null || amount is null)
						return null;

					var kind = ctx.Ldb.GetValueOrDefault((LdbMetaTypeCategory, metaType.Value));
					return kind == null ? null : [ctx.Magnitude(MetaTypeStatId, (int)amount.Value), kind];
				}

				// `Knockback %d meters` - one conversion, and the branch (0x014a1542) fills it from key 238 FIRST.
				// ⚠️ It reads key 191 as well and hands it to a second format call that has no slot left to fill, so
				// the drawn number is 238 and 191 does not appear. Binding the two the other way round would put a
				// resistance-shaped number where the metres go.
				case KnockbackKind:
				{
					var metres = element.Value(KeyKnockbackMetres);
					return metres is null ? null : [(int)metres.Value];
				}

				// `Team Formula %s` / `Team effect %s` - one slot, and the branch reads exactly key 88.
				case TeamFormulaKind:
				case TeamPropagatedBuffKind:
				{
					var teamSpell = element.Value(GameActionTypes.KeySpell);
					return teamSpell is null ? null : [$"{ctx.SpellName(teamSpell.Value)} ({teamSpell.Value})"];
				}

				// `Resurrect target with %d% health` - one conversion, and the branch (0x014a1594) reads one slot.
				case ResurrectTargetKind:
				{
					var health = element.Value(KeyResurrectHealth);
					return health is null ? null : [(int)health.Value];
				}

				// `#1{1:Increases | Decreases} your #2%s by #3%d` - the sentence itself pins all three: a sign
				// selector, the stat's name and an integer magnitude, and the branch (0x014a1a2e) reads exactly
				// (Stat, Value). ⚠️ `%d`, so the magnitude is the RAW rating - not run through the display scale.
				case ModifyAsRatingKind:
				{
					var ratingStat = element.Value(GameActionTypes.KeyStat);
					var ratingValue = element.Value(GameActionTypes.KeyValue);
					if (ratingStat is null or 0 || ratingValue is null)
						return null;

					var signed = (int)ratingValue.Value;
					return [signed > 0 ? 1 : 0, ctx.StatName(ratingStat.Value), Math.Abs(signed)];
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
			IReadOnlyList<ItemTemplateStatModifiers.Element> elements, int textBytes, int pairRegionBytes)
		{
			var body = record.Length - 12;
			var elementBytes = elements.Sum(e => e.WidthBytes);
			// ⭐ The whole PAIR REGION, not just the property run: the 20-byte prefix, the head's own pairs, the run
			// and any trailing resource reference. Its length IS where the string block starts, which the record
			// states in its frame word - `(pairs + 1) * 0x3F1` at byte 16 (#632).
			var fieldBytes = pairRegionBytes;
			// The 12-byte `[2][listId][(n + 1) * 0x3F1]` header in front of each element group (#785). Counted here
			// because it IS decoded now - leaving it out reported the record as less accounted-for than it is, and it
			// is what says which of two identical-looking elements belong to the same list.
			var lists = ItemTemplateStatModifiers.ParseElementLists(record.AsSpan(12));
			var listBytes = lists.Count * 12;
			// #632: the record's own name + description, immediately after the property run. Measured by the caller,
			// which already has the run end.
			var listFieldBytes = ScanListFields(record.AsSpan(12), pairRegionBytes + textBytes);
			var accounted = elementBytes + fieldBytes + listBytes + textBytes + listFieldBytes;

			yield return "--";
			yield return $"record {body} B body: {elements.Count} element(s) in {lists.Count} list(s) {elementBytes} B + {listBytes} B header "
			             + $"+ {fields.Count} property field(s) in a {fieldBytes} B head+pair region + {textBytes} B name/description "
			             + $"+ {listFieldBytes} B list fields "
			             + $"= {accounted} B ({(body == 0 ? 0 : 100.0 * accounted / body):0.#}%), {body - accounted} B unaccounted";

			var unparsed = ItemTemplateStatModifiers.UnparsedElementHeads(record.AsSpan(12));
			if (unparsed.Count > 0)
				yield return $"⚠️ {unparsed.Count} element head(s) the scan did not account for: {string.Join(", ", unparsed.Take(8))}";

			// ⚠️ The OTHER half of the accounting, and the one the byte total cannot show: an element whose head sits
			// INSIDE another element's payload. The scan anchors byte by byte and does not skip an element's own span,
			// so a sub-record run holding a type-id-shaped word plus a `4` yields a second, nested "element" - and
			// because the line above SUMS the widths, two overlapping elements make the record look BETTER accounted
			// for than it is. Reported as a span pair, because the fix for a real one is to read the outer element's
			// grammar rather than to filter the inner one out (#783).
			var overlaps = new List<string>();
			for (var i = 1; i < elements.Count; i++)
			{
				var previous = elements[i - 1];
				if (elements[i].Offset < previous.Offset + previous.WidthBytes)
					overlaps.Add($"{elements[i].Kind}@{elements[i].Offset} inside {previous.Kind}@{previous.Offset}+{previous.WidthBytes}");
			}

			if (overlaps.Count > 0)
				yield return $"⚠️ {overlaps.Count} element(s) nested inside another's payload: {string.Join(", ", overlaps.Take(8))}";

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

		/// <summary>
		///     Bit 24 of that same flags word: the client's HIDE-FROM-TOOLTIP flag on a spell (#789).
		/// </summary>
		/// <remarks>
		///     <para>
		///         `FUN_014a3b30(spellId)` loads the referenced spell through `SpellTemplate_t::GetMagicSpell` and
		///         returns `*(byte*)(spell + 0xCB) &amp; 1`. ⭐ `+0xCB` is byte 3 of the dword at `+0xC8`, which is the
		///         SAME flags word #629/#630 already reads bit 2 of for the mana mode. So the flag needed no new
		///         field: it is bit 24 of a word we were already reading.
		///     </para>
		///     <para>
		///         ⚠️ That word is located by `RdbSpellImporter.ReadFlagsWord`, five words BEFORE the property run -
		///         not at a fixed record offset. The head is variable-length, and the fixed form read 0 on 936
		///         records, which forced their bit clear whatever the record said (#632).
		///     </para>
		///     <para>
		///         Two branches consult it, and neither is cosmetic. `ConeFormula` calls it on key 88 and, when set,
		///         skips its whole sentence (falling into the shared tail, so a periodic element still draws its
		///         "every N s" suffix and nothing else). `AddComboProc` calls it on key 88 and key 236 separately and
		///         treats a flagged side as ABSENT, so a two-sided proc draws as a one-sided one.
		///     </para>
		/// </remarks>
		private const int HiddenFromTooltipBit = 24;

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
		private const uint AddComboProcKind = 53212;
		private const uint AddPercentSkillModifierKind = 53189;
		private const uint AreaFormulaKind = 53087;
		private const uint AttackAddTargetsKind = 53273;
		private const uint CastNanoKind = 53051;
		private const uint ColumnAddTargetsKind = 53249;
		private const uint ConeFormulaKind = 53259;
		private const uint ChangeHitPercentKind = 53030;
		private const uint ChangeLineHitPercentKind = 53075;
		private const uint FilteredAreaFormulaKind = 53088;
		private const uint HitKind = 53002;
		private const uint HitPercentageKind = 53007;
		private const uint KnockbackKind = 53217;
		private const uint NanoResistanceKind = 53162;
		private const uint ModifyAsRatingKind = 53067;
		private const uint ResurrectTargetKind = 53227;
		private const uint TeamFormulaKind = 53066;
		private const uint TeamPropagatedBuffKind = 53224;

		// `AddPercentSkillModifier`'s two stat slots. Per-TYPE like every unnamed key: on `UpgradeCombo` 66/67 are the
		// damage and cooldown deltas, here they are the SOURCE stat and the stat it raises (see the branch note).
		private const int SkillModifierSourceKey = 66;
		private const int SkillModifierTargetKey = 67;

		// The 0.01 `FUN_014af090` applies to both of `ColumnAddTargets`'s magnitudes on its way out. Named here rather
		// than written as a bare /100 because it belongs to the GETTER, not to either key.
		private const double ColumnScale = 0.01;

		// `ColumnAddTargets`'s optional distance-to-target (key 238). Per-TYPE, like every unnamed key: 238 is the
		// knockback distance on `Knockback`.
		private const int KeyColumnDistance = 238;

		// The cone kinds' arc in DEGREES (key 213, stat 0x4DD). Its partner key 212 is the negative half of it and is
		// the one slot neither cone branch reads.
		private const int KeyConeArcDegrees = 213;

		// `ResurrectTarget`'s only payload slot - the health percentage the target comes back at.
		private const int KeyResurrectHealth = 188;

		// `HitPercentage`'s MODE word: 1 or 2 sends the client down a path that reads the caster's own stat, so those
		// two values are not renderable from the record.
		private const int KeyHitPercentMode = 32;

		// `Knockback`'s distance in metres (key 238) - the slot the client fills its one `%d` from. Per-TYPE.
		private const int KeyKnockbackMetres = 238;

		// `NanoResistance`'s first slot: an id into LDB 55200, the client's spell-effect / METATYPE catalog.
		private const int KeyNanoResistanceMetaType = 152;

		// LDB category 55200 - what a `NanoResistance` element's key 152 names ("Poison/Electrical resistance rating
		// buff", "Damage - Poison Instant DD", ...). Not the stat-name category.
		private const uint LdbMetaTypeCategory = 55200;

		// 0x4B `MetaType` - the stat whose DISPLAY SCALE the client renders a `NanoResistance` amount through.
		private const uint MetaTypeStatId = 0x4B;

		// 0x1F8 Stamina - the one non-Health stat `HitPercentage` gives its own phrasing to.
		private const uint StaminaStatId = 0x1F8;
		private const uint InterpolatedHitKind = 53219;
		private const uint SetSpellRecastTimeKind = 53226;
		private const uint SpecialHitKind = 53196;

		// The `Hit` family's magnitude slots. ⚠️ Per-TYPE names, like every unnamed key: 170 is the target CAP on the
		// area kinds and the high magnitude's LOW endpoint here.
		private const int KeyHitMax = 37;
		private const int KeyHitMaxHigh = 171;
		private const int KeyHitMaxLow = 170;
		private const int KeyHitMin = 2;
		private const int KeyHitMinHigh = 169;
		private const int KeyHitMinLow = 168;
		private const int KeyRampHighLevel = 173;
		private const int KeyRampLowLevel = 172;

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
				// ⭐ A kind may draw itself with ANOTHER type's sentence, and several of the most common ones do -
				// `InterpolatedModify` renders through `Modify`, the whole spell-stat-modifier family through
				// `SpellStatModifier`. See GameActionTooltip.SentenceKinds (#782).
				if (Ldb.TryGetValue((LdbGameActionFormatCategory, GameActionTooltip.SentenceKind(kind)), out var byId))
					return byId;

				var name = GameActionTypes.Get(kind)?.Name;
				return name != null ? SentenceForToken(name) : null;
			}

			/// <summary>The sentence keyed by a TOKEN NAME - see <see cref="SentenceFor" />'s remarks.</summary>
			public string? SentenceForToken(string token)
			{
				return Ldb.GetValueOrDefault((LdbGameActionFormatCategory, Elf28.Of(token)));
			}

			/// <summary>
			///     A magnitude RANGE as the client draws one - `FUN_0149a700`'s `min-max`, through each endpoint's
			///     display scale.
			/// </summary>
			/// <remarks>
			///     ⚠️ Drawn as a MAGNITUDE, not signed: the client's hit branch takes the absolute value when the low
			///     endpoint is negative, because the sentence around it already says "points of" or "Removes". Signing
			///     it here would render "-58--71 points of Health".
			///     <para>
			///         ⚠️ EQUAL endpoints collapse to one number. That is the client's own branch, not a tidy-up:
			///         `FUN_0149a700` compares its two arguments and takes a separate path when they match, so a flat
			///         magnitude draws as "20", never "20-20".
			///     </para>
			/// </remarks>
			public string Range(uint statId, int min, int max)
			{
				var scale = StatDisplayScale.Of(statId);
				var (low, high) = min < 0 ? (-min, -max) : (min, max);
				var suffix = StatUnitSuffix.SuffixOf(statId);
				return low == high
					? $"{low / (double)scale:0.##}{suffix}"
					: $"{low / (double)scale:0.##}-{high / (double)scale:0.##}{suffix}";
			}

			/// <summary>
			///     The same range with NO divisor and no unit - `FUN_0149a700(min, max, 0)`.
			/// </summary>
			/// <remarks>
			///     ⚠️ Whether the range divides is the builder's THIRD argument, and it is per CALL SITE, not per stat:
			///     the `Hit` family passes the stat's own percent predicate, `HitPercentage` passes a literal 0. Reusing
			///     <see cref="Range" /> there would report a 30% drain as 0.3%.
			/// </remarks>
			public string RawRange(int min, int max)
			{
				var (low, high) = min < 0 ? (-min, -max) : (min, max);
				return low == high ? low.ToString(CultureInfo.InvariantCulture) : $"{low}-{high}";
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

			/// <summary>
			///     Whether the spell a tooltip element REFERENCES carries <see cref="HiddenFromTooltipBit" /> - the bit
			///     the client's own `FUN_014a3b30` reads to decide the element draws nothing (#789).
			/// </summary>
			/// <remarks>
			///     It loads the OTHER spell's record to answer, which is why an element's own bytes cannot decide it.
			///     A spell with no record at all is not flagged - the client's `GetMagicSpell` returns null there and
			///     the predicate answers 0.
			/// </remarks>
			public bool SpellHiddenFromTooltip(uint spellId)
			{
				if (spellId == 0 || !Rdb.TryGet(SpellTemplateType, spellId, out var entry))
					return false;

				byte[] record;
				try
				{
					record = Rdb.ReadDecompressed(entry);
				}
				catch
				{
					return false;
				}

				RdbSpellImporter.ReadIncreasingFields(record.AsSpan(12), out var runStart, out _);
				return (RdbSpellImporter.ReadFlagsWord(record.AsSpan(12), runStart) >> HiddenFromTooltipBit & 1) == 1;
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-flags`: sweeps the spell record's FLAGS word bit by bit against things the DB knows, so
	// "which bit says X" is a question that can be ASKED rather than argued (#632).
	//
	// The word sits five words before the property run (`RdbSpellImporter.ReadFlagsWord`), and two of its bits are
	// already named: **bit 2** is the mana-cost mode (#629/#630) and **bit 24** the hide-from-tooltip bit (#789).
	//
	// ⭐ Bit 2 is printed FIRST and deliberately, as the CONTROL. It reproduces `Spell.ManaCostIsPercent` exactly -
	// 1,778 set against 1,778, zero disagreement - which is what a bit that IS a predicate looks like. Every other
	// row is read against that yardstick: 99% is a lead, 100% is a definition. Without the control a 99% row reads
	// as an answer, which is the mistake this report exists to prevent.
	public static partial class ReportCli
	{
		// One record's flags word beside every predicate the sweep scores it against. `Duration` is the record's OWN
		// field 8 rather than the `DurationMs` column, so a disagreement cannot be a column blend (#632).
		private sealed record FlagsRow(uint Flags, bool Duration, bool Cast, bool Mana, bool ManaPercent, bool Trainable,
			bool Mount, bool Area, bool Combo, bool Proc, bool Criteria, bool Elements, bool CastNano, uint Id,
			uint RawField8, string Name);

		// ⭐ NAMED (#632): bit 16 GATES THE CLIENT'S TIMED-EFFECT PATH. `FUN_007ee710`'s bit-set branch is the only
		// place that computes a duration at all - it fetches stat 0x1D0 `DurationModifier`, scales by it and by 0.01,
		// and lands the result in a longlong time. The sweep had it as a 99.07% association with "the record carries
		// field 8"; the client turns that into a mechanism, and the mechanism explains the residual the association
		// could not: the 131 records that state a duration with the bit CLEAR store a value that is NEVER APPLIED,
		// because the code that would apply it sits behind the bit. `VisualBuffTest` heading that list is what a
		// never-applied duration looks like. See docs/rdb/spell-record-fields.md.
		private const int DurationBit = 16;

		// The spell record's own duration property (`docs/rdb/spell-record-fields.md`).
		private const uint DurationFieldId = 8;

		// The three PROC GameAction kinds (`docs/re/gameaction-type-ids.tsv`) - a record carrying one of these IS a
		// proc, which is the "is-a-proc" predicate #632 named and no DB column supplies.
		private static readonly HashSet<uint> ProcKinds = [53211, 53212, 53213];

		/// <summary>
		///     Which of the UNNAMED bits the client reads at all, and where - the census that turns "30 unnamed bits"
		///     into a short list with addresses (#632).
		/// </summary>
		/// <remarks>
		///     ⭐ Produced by `tools/ghidra/DumpFlagBitReads.java` over the whole image:
		///     <code>
		///     OFFSET=c8 WINDOW=12 /opt/ghidra/support/analyzeHeadless &lt;data-repo&gt;/Ghidra aoc \
		///       -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpFlagBitReads.java
		///     </code>
		///     ⚠️ **`+0xC8` is a common displacement and the script says so**, so the sites are classified by ADDRESS
		///     RANGE rather than counted. The magic subsystem is ~`0x0075xxxx-0x007Fxxxx` with the combat half around
		///     `0x0114xxxx` (`FUN_0114efa0` is `DealDamage`, self-named); anything else reading `+0xC8` is a
		///     different struct wearing the same offset.
		///     <para />
		///     ⛔ That reclassifies the sweep's own biggest lead. **Bit 0 is set on 16,229 of 20,847 records - 78%,
		///     the second most common bit in the word - and NOTHING in the spell subsystem reads it.** Its one hit is
		///     `__tolower_l`, a CRT function. So it is not a decode gap at all, and a predicate score against it can
		///     only ever be a coincidence. Bit 3 is the same story (`__atof_l`).
		///     <para />
		///     ⚠️ Bits 5, 6 and 7 are read ONLY inside `FUN_017594b0/01759690/01759a70/01759e30` - four adjacent
		///     functions reading four adjacent bits, which is a bitfield of its own at the same displacement. Bit 8
		///     appears there too AND in the magic subsystem, which is what identifies that cluster as the other
		///     struct rather than as more spell reads.
		/// </remarks>
		private static readonly string[] UnnamedBitReadSites =
		[
			"  the UNNAMED bits, by whether the client reads them AT ALL (see UnnamedBitReadSites):",
			"    bit  0  ⛔ NOT READ - 78% of records carry it and its only hit is __tolower_l. Not a decode gap",
			"    bit  3  ⛔ NOT READ - __atof_l and one CRT-range site",
			"    bit  5  ⚠️ only in FUN_01759{4b0,690,a70,e30} - a different struct at the same +0xC8, not this word",
			"    bit  6  ⚠️ only in FUN_01759{4b0,690,a70,e30} - likewise",
			"    bit  7  ⚠️ only in FUN_01759{4b0,690,e30} - likewise",
			"    bit  8  ⭐ READ: FUN_007e9b50 (the feedback-verb builder bit 15 is named from) + FUN_007ebe70",
			"    bit 14  ⭐ READ: FUN_01141480 / 011417f0 / 01141c30 - the combat cluster, two of which also read bit 15",
			"    bit 19  ⭐ READ: FUN_007ebe70, sharing ONE guard with bit 8 - `bit19 == 0 && bit8 == 0` gates the same block",
			"    bit 20  ⭐ READ: FUN_007ee710 (the timed-effect path of bit 16) + FUN_00769220 (which also reads bit 15)",
			"    bit 21  ⭐ READ: FUN_007f17d0 / FUN_007f1860",
			"    bit 29  ⭐ READ: FUN_007d7ee0",
			"    the rest  no read site anywhere in the image",
		];

		/// <summary>
		///     The bits whose meaning came out of the CLIENT rather than out of this sweep, printed in the header so
		///     the table is read beside them (#632).
		/// </summary>
		/// <remarks>
		///     ⭐ A bit named from the disassembly does not need a good predicate score, and a score that disagrees
		///     with the code is the DATA being described rather than the bit being redefined. Bit 20 is the case that
		///     makes the point: it scores +9.3 WITH "carries a duration", which reads as a contradiction of
		///     "permanent" until you see that the client's three consumers are all about a stated duration being
		///     neither shown nor counted down - a record has to state one for the bit to have anything to suppress.
		/// </remarks>
		private static readonly (int Bit, string Meaning)[] NamedBits =
		[
			(1, "the spell OPTS OUT of the global spell modifier - `FUN_014a9f90` skips the Ferocity (stat 0x57B) scaling "
			    + "when it is set, unless the spell is on the `GlobalModIncludedSpellLine` tuning line; the apply side "
			    + "(`FUN_007f5360`) runs the same predicate negated"),
			(2, "the mana cost is a PERCENT of the caster's pool (#629/#630) - the control row"),
			(15, "the spell is HOSTILE. The client's combat-feedback builders pick their VERB on it and say so in "
			     + "English: `FUN_007E9B50` does `if ((flags >> 0xf & 1) == 0) \"YouAffectOtherByEffect\" else "
			     + "\"YouAfflictOtherByEffect\"`, and the same if/else again for `OtherAffectsYouByEffect` / "
			     + "`OtherAfflictsYouByEffect` - affect when clear, AFFLICT when set. `FUN_007F4120` uses it a second "
			     + "way that agrees: it selects a targeting mode (5 when clear, 3 when set). 12,069 of 20,847 spells "
			     + "carry it"),
			(16, "the client RE-CREATES this magic and computes its duration (`FUN_007ee710`, stat 0x1D0 DurationModifier)"),
			(20, "the magic's stated duration is neither DRAWN nor counted down - the tooltip's duration line renders empty "
			     + "(`FUN_01499b10`) and `CoreMagicHolder::UpdateBuff` suppresses its 'remaining time' warning when it "
			     + "expires; it rides into the spawn apply beside bit 16 (`FUN_007f43a0`)"),
			(4, "SHAPE ONLY. Its single read site `FUN_007e0820` writes 5 instead of 1 into one field of a struct it "
			    + "builds - the same 1/3/5 mode family bit 15 selects from in `FUN_007f4120`. The enum is not "
			    + "recovered, so this is a narrowing rather than a name (249 records)"),
			(11, "a HOSTILE spell that does NOT raise the hostile-act signal. `CoreMagicHolder::CastMagicSpell()` "
			     + "(`FUN_007ea480`, named by its own log text) ends in `if (bit26 == 0) { if (bit15 == 0 || bit11 || "
			     + "bit1) emitA(); else if (target is CoreCharacter) emitB(); }`, and emitB (`FUN_007f8100`) is emitted "
			     + "from exactly two places in the whole image: that branch and DealDamage (`FUN_0114efa0`, likewise "
			     + "self-named). So bit 11 diverts a hostile cast onto the benign path. ⚠️ It is only OPERATIVE on the "
			     + "523 of its 2,086 records that are hostile - the rest take that branch on bit 15 alone"),
			(23, "`NoAutomaticReplace` - refuse the cast when the target already has THIS spell running. See bit 30 for "
			     + "how the two names split"),
			(24, "hide this spell's line from a tooltip (#789)"),
			(26, "the cast raises NO post-cast notification at all, and it changes which record the stacking "
			     + "comparison reads. It is the outer guard of the `CastMagicSpell` block quoted under bit 11 - set, and "
			     + "neither signal fires - and in `FUN_007ef6b0` it selects which object\'s `+0x34` is compared against "
			     + "the stacking value. ⭐ The data says what kind of record that is: 264 spells, 258 of them "
			     + "NON-hostile, 238 of them (90%) one half of a same-NAME pair whose twin lacks the bit - against a "
			     + "~23% twin rate catalog-wide - and the lowest-id member is the designers\' own "
			     + "`TeamPropagatedBuff Test Slave`. So it marks the propagated/derived copy of a buff rather than the "
			     + "cast a player performs. Check it with `--report spell-flags --report-arg 26`"),
			(30, "`NoAutomaticStackReplace` - refuse the cast when the target already has ANY spell of the same "
			     + "stackingline running. ⭐ This SPLITS the 23/30 pair, which was recorded as inseparable. "
			     + "`FUN_007ef6b0` names two flags in its own log text - \"since its flagged as NoAutomaticReplace and "
			     + "the target already have it running\" and \"since its flagged as NoAutomaticStackReplace and the "
			     + "target already have a spell in the stackingline running\" - and it tests exactly three bits of this "
			     + "word: 23, 26 and 30. The StackReplace message is guarded on bit 30 ALONE, which pins it; two names "
			     + "over two bits then leaves Replace to 23, and the semantics agree (the exact-spell lookup vs the "
			     + "stackingline lookup). ⚠️ The Replace message itself fires under `bit23 || bit30`, so its own guard "
			     + "does not separate them - the split rests on bit 30\'s solo guard plus that elimination")
		];

		// The floor and the threshold for the bit ⊂ bit table. A bit set a handful of times is inside almost anything
		// by luck; 98% rather than 100% because one straggler is a data edge, not a broken relation.
		private const int ContainmentFloor = 100;
		private const double ContainmentThreshold = 0.98;

		private static async Task SpellFlags(string rdbDir, string? arg)
		{
			using var rdb = OpenRdb(rdbDir);
			await using var db = OpenDb();

			var spells = await db.Spells.AsNoTracking()
				.Select(s => new { s.Id, s.DurationMs, s.CastTime, s.ManaCost, s.ManaCostIsPercent })
				.ToDictionaryAsync(s => s.Id, s => s);
			var trainable = (await db.ClassSpells.AsNoTracking().Select(c => c.SpellId).Distinct().ToListAsync()).ToHashSet();
			var mounts = (await db.Mounts.AsNoTracking().Select(m => m.Id).ToListAsync()).ToHashSet();
			var areas = (await db.SpellAreaEffects.AsNoTracking().Select(a => a.SpellId).Distinct().ToListAsync()).ToHashSet();
			// ⭐ The SHARPER predicates #632 asked for. The first three come from the record itself rather than from a
			// column, which is what makes them worth adding: a bit that means "this is a proc" has to agree with the
			// record CARRYING a proc element, and no DB column says that.
			var combos = (await db.Combos.AsNoTracking().Select(c => c.GameActionId).Distinct().ToListAsync()).ToHashSet();

			var rows = new List<FlagsRow>();
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

				if (record.Length < 12 || !spells.TryGetValue(entry.Id, out var spell))
					continue;

				var body = record.AsSpan(12);
				RdbSpellImporter.ReadIncreasingFields(body, out var runStart, out var runEnd);
				if (runStart < 20)
					continue;

				var elements = ItemTemplateStatModifiers.ParseElements(body);
				// ⭐ The record's OWN duration field, not the `DurationMs` column - that column also takes values from
				// the wire and the effect catalog, so a disagreement read against it could be a column blend rather
				// than the bit being wrong (#632).
				var fields = RdbSpellImporter.ReadIncreasingFields(body, out _);
				var rawDuration = fields.GetValueOrDefault(DurationFieldId);
				var (recordName, _, _) = RdbSpellImporter.ReadStringBlock(body, runEnd);
				rows.Add(new FlagsRow(RdbSpellImporter.ReadFlagsWord(body, runStart), rawDuration > 0, spell.CastTime > 0,
					spell.ManaCost > 0, spell.ManaCostIsPercent, trainable.Contains(entry.Id), mounts.Contains(entry.Id),
					areas.Contains(entry.Id), combos.Contains(entry.Id),
					elements.Any(e => ProcKinds.Contains(e.Kind)), elements.Any(e => e.Criteria.Any()),
					elements.Count > 0, elements.Any(e => e.Kind == CastNanoKind), entry.Id, rawDuration, recordName));
			}

			// A predicate is worth a column when it is neither rare nor universal - one that is true of everything
			// cannot separate any bit from any other.
			var predicates = new (string Name, Func<FlagsRow, bool> Of)[]
			{
				("duration", r => r.Duration), ("cast", r => r.Cast), ("mana", r => r.Mana),
				("mana%", r => r.ManaPercent), ("train", r => r.Trainable), ("mount", r => r.Mount), ("area", r => r.Area),
				("combo", r => r.Combo), ("proc", r => r.Proc), ("criteria", r => r.Criteria),
				("elements", r => r.Elements), ("castnano", r => r.CastNano)
			};

			Console.WriteLine($"spell FLAGS-word bit sweep over {rows.Count:N0} records (#632)");
			Console.WriteLine("  agreement = how often the BIT and the predicate say the same thing. Bit 2 is the CONTROL:");
			Console.WriteLine("  it is a known predicate (ManaCostIsPercent, #630), so every other row is read against it.");
			Console.WriteLine();
			// ⭐ What the CLIENT does with a bit, printed beside what the DATA correlates it with. A bit whose meaning
			// came out of the disassembly does not need a predicate to score well, and a predicate score that
			// disagrees with the code is the data being described, not the bit being redefined - which is exactly the
			// shape of bit 20 below.
			Console.WriteLine("  bits named from the CLIENT's own reads (tools/ghidra/DumpFlagBitReads.java finds the sites):");
			foreach (var (bit, meaning) in NamedBits.OrderBy(n => n.Bit))
				Console.WriteLine($"    bit {bit,2}  {meaning}");

			Console.WriteLine();
			foreach (var line in UnnamedBitReadSites)
				Console.WriteLine(line);

			Console.WriteLine();
			Console.WriteLine("  ⚠️ A RARE bit scores high against a rare predicate for free: both are mostly false, so they");
			Console.WriteLine("  agree on every record that has neither. Read the `set` column first - a bit set on 4 records");
			Console.WriteLine("  scoring 99% against `mount` (155 records) is the base rate, not a finding.");
			Console.WriteLine();
			// ⭐ The base-rate correction the warning above only ASKED for. Two mostly-false columns agree on every
			// record that has neither, so a raw percentage rewards rarity. `expected` is what a bit of this bit's own
			// rarity would score against this predicate's own prevalence by chance alone; EXCESS is the difference,
			// and it is the only number in this table that means anything on its own.
			var prevalence = predicates.Select(p => (double)rows.Count(p.Of) / rows.Count).ToArray();
			Console.WriteLine($"  predicate prevalence: " + string.Join("  ", predicates.Select((p, i) => $"{p.Name}={100 * prevalence[i]:0.0}%")));
			Console.WriteLine();
			Console.WriteLine($"  EXCESS over chance, in points. The control (bit 2 vs mana%) is the yardstick.");
			Console.WriteLine($"  {"bit",3} {"set",7}   " + string.Join("  ", predicates.Select(p => $"{p.Name,-9}")));

			var only = uint.TryParse((arg ?? "").Trim(), out var parsed) ? parsed : (uint?)null;
			for (var bit = 0; bit < 32; bit++)
			{
				if (only is { } wanted && wanted != bit)
					continue;

				var set = rows.Count(r => (r.Flags >> bit & 1) == 1);
				if (set == 0)
					continue;

				var bitRate = (double)set / rows.Count;
				var cells = predicates.Select((p, i) =>
				{
					var agree = (double)rows.Count(r => ((r.Flags >> bit & 1) == 1) == p.Of(r)) / rows.Count;
					var expected = bitRate * prevalence[i] + (1 - bitRate) * (1 - prevalence[i]);
					var excess = 100 * (agree - expected);
					// A cell at or below chance carries no information; printing it as a number invites reading it
					// as one, so it prints as a dot.
					return excess < 1.0 ? $"{".",8}  " : $"{excess,7:+0.0;-0.0} ";
				});

				Console.WriteLine($"  {bit,3} {set,7:N0}   " + string.Join("  ", cells));
			}

			if (only is { } drill)
				BitMembers(rows, (int)drill);

			// ⭐ The strongest row, examined rather than summarised. A bit that IS a predicate has a residual you can
			// characterise; one that merely correlates has a residual that looks like the rest of the catalog.
			DurationBitResidual(rows);
			BitContainment(rows);
		}


		// The spells carrying one bit, NAMED, plus the cross-tab against bit 15 (hostile).
		//
		// ⭐ It exists because the predicate table cannot check a bit the client named. Bits 11 and 26 were read out
		// of `CoreMagicHolder::CastMagicSpell()` as modifiers of the HOSTILE path (#632), and a claim about the
		// hostile path is only checkable against which spells are hostile - which is bit 15, not a DB column. So the
		// cross-tab is the assertion and the names are the sanity check on it.
		//
		// ⚠️ The names are a SAMPLE and the counts are not. Reading a mechanism off the first 40 rows is the mistake
		// the header of this report exists to prevent; the cross-tab above them is the measurement.
		private static void BitMembers(IReadOnlyList<FlagsRow> rows, int bit)
		{
			const int HostileBit = 15;
			const int Sample = 40;

			var withBit = rows.Where(r => (r.Flags >> bit & 1) == 1).ToList();
			if (withBit.Count == 0)
				return;

			int Both(bool b, bool h) => rows.Count(r => ((r.Flags >> bit & 1) == 1) == b && ((r.Flags >> HostileBit & 1) == 1) == h);

			Console.WriteLine();
			Console.WriteLine($"  bit {bit} x bit {HostileBit} (hostile):");
			Console.WriteLine($"    {"",14} {"hostile",9} {"not",9}");
			Console.WriteLine($"    {$"bit {bit} set",14} {Both(true, true),9:N0} {Both(true, false),9:N0}");
			Console.WriteLine($"    {$"bit {bit} clear",14} {Both(false, true),9:N0} {Both(false, false),9:N0}");

			// ⚠️ The two numbers a name-shaped hypothesis dies on. A bit that marks the "slave" copy of a
			// duplicated spell has to be BOTH untrainable and twinned; measuring only the sample the listing prints
			// is how a mixed set reads as a clean one - `Aura of Infusion`'s bit-26 twin is untrainable and
			// `Divine Vigor`'s is not, and eight rows cannot tell those apart from a rule.
			var byName = rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.ToList());
			var twinned = withBit.Count(r => byName[r.Name].Any(o => o.Id != r.Id && (o.Flags >> bit & 1) == 0));
			Console.WriteLine($"    trainable: {withBit.Count(r => r.Trainable):N0} / {withBit.Count:N0}" +
			                  $"   (catalog-wide {rows.Count(r => r.Trainable) * 100.0 / rows.Count:0.0}%)");
			Console.WriteLine($"    has a same-NAME twin WITHOUT the bit: {twinned:N0} / {withBit.Count:N0}");

			Console.WriteLine();
			Console.WriteLine($"  {withBit.Count:N0} spell(s) with bit {bit}; first {Math.Min(Sample, withBit.Count)} by id:");
			foreach (var row in withBit.OrderBy(r => r.Id).Take(Sample))
			{
				var hostile = (row.Flags >> HostileBit & 1) == 1 ? "hostile" : "-";
				Console.WriteLine($"    {row.Id,8}  {hostile,-7}  0x{row.Flags:X8}  {row.Name}");
			}
		}

		// ⭐ Which bits are SUBSETS of other bits - the question a bit-vs-predicate table structurally cannot ask.
		//
		// It matters because the client's own code pairs bits: bit 20 (a duration that is not counted down) is only
		// meaningful on a magic the client re-creates, which is bit 16. A containment relation is evidence the two
		// belong to one mechanism; its absence is evidence they do not, and both readings are unavailable from the
		// predicate table because a predicate can only compare a bit to the DATA.
		//
		// ⚠️ Containment is DIRECTED and the small side is the informative one - every bit is trivially "contained"
		// in a bit that is set almost everywhere. The floor on |A| is what keeps a 4-record bit out of the list.
		private static void BitContainment(IReadOnlyList<FlagsRow> rows)
		{
			var setOf = new List<uint>[32];
			for (var bit = 0; bit < 32; bit++)
				setOf[bit] = rows.Where(r => (r.Flags >> bit & 1) == 1).Select(r => r.Id).ToList();

			var found = new List<string>();
			for (var a = 0; a < 32; a++)
			{
				if (setOf[a].Count < ContainmentFloor)
					continue;

				var members = setOf[a].ToHashSet();
				for (var b = 0; b < 32; b++)
				{
					if (b == a || setOf[b].Count <= setOf[a].Count)
						continue;

					var inside = setOf[b].Count(members.Contains);
					if (inside >= ContainmentThreshold * setOf[a].Count)
						found.Add($"    bit {a,2} ({setOf[a].Count,6:N0}) is {100.0 * inside / setOf[a].Count,6:0.00}% inside bit {b,2} ({setOf[b].Count,6:N0})");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"  ⭐ bit ⊂ bit containment, at or above {100 * ContainmentThreshold:0}% and on bits set at least {ContainmentFloor} times:");
			if (found.Count == 0)
				Console.WriteLine("     none - no bit in this word is a near-subset of another");
			else
				foreach (var line in found)
					Console.WriteLine(line);
		}

		// ⭐ Bit 16 against the record's own duration field, and what the disagreements ARE (#632).
		private static void DurationBitResidual(IReadOnlyList<FlagsRow> rows)
		{
			var setNoDuration = rows.Where(r => (r.Flags >> DurationBit & 1) == 1 && !r.Duration).ToList();
			var durationNotSet = rows.Where(r => (r.Flags >> DurationBit & 1) == 0 && r.Duration).ToList();

			Console.WriteLine();
			Console.WriteLine($"  ⭐ bit {DurationBit} vs the record's OWN field 8, residual broken out:");
			Console.WriteLine($"     bit set, no duration : {setNoDuration.Count,5:N0}");
			Console.WriteLine($"     duration, bit clear  : {durationNotSet.Count,5:N0}   <- the direction that would DISPROVE it");
			Console.WriteLine($"     agree                : {rows.Count - setNoDuration.Count - durationNotSet.Count,5:N0} of {rows.Count:N0}");

			// The asymmetry is the finding: a flag that means "carries a duration" may legitimately be set on a
			// record whose duration is supplied elsewhere, but it must never be CLEAR on one that states its own.
			foreach (var (label, sample) in new[] { ("bit set, no duration", setNoDuration), ("duration, bit clear", durationNotSet) })
			{
				if (sample.Count == 0)
					continue;

				Console.WriteLine($"     {label} - first few:");
				foreach (var r in sample.OrderBy(r => r.Id).Take(6))
					Console.WriteLine($"        {r.Id,-9} field8={r.RawField8,-9} {(r.Name.Length > 44 ? r.Name[..44] : r.Name)}");
			}

			// ⚠️ Does the residual cluster on the duration's VALUE? If the exceptions were "durations of a particular
			// kind" they would; if they look like the rest of the catalog, the simple reading is just wrong.
			var agreeing = rows.Where(r => (r.Flags >> DurationBit & 1) == 1 && r.Duration).Select(r => r.RawField8).ToList();
			foreach (var (label, values) in new[] { ("agreeing", agreeing), ("bit clear", durationNotSet.Select(r => r.RawField8).ToList()) })
			{
				var sorted = values.Order().ToList();
				if (sorted.Count == 0)
					continue;

				Console.WriteLine($"     field 8 on the {label,-9} set: n={sorted.Count,-6} min={sorted[0],-8} median={sorted[sorted.Count / 2],-9} max={sorted[^1]}");
			}
		}
	}
}

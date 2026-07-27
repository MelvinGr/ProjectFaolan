using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Fills <see cref="ClassAttributeBase" /> - the class/level BASE a character's stats start from (#444).
	/// </summary>
	/// <remarks>
	///     The base is not on the wire (retail sends a flat 10 for every attribute), and it is not in any of the client's
	///     named data tables either. It lives in RDB type <b>1000020</b>, the ITEM-template type, as 80 PLAINTEXT templates
	///     per class - one per level - whose stat records are read by the ordinary item parser
	///     (<see cref="ItemTemplateStatModifiers" />).
	///     <para>
	///         <b>The level is recovered from the template, not from its id.</b> Each one names itself
	///         <c>"&lt;Class&gt; Class Feat &lt;NNN&gt;"</c>, and <c>NNN</c> is the first level of a TEN-LEVEL BLOCK: 001,
	///         011, 021 … 071. The ten members of a block are ordered within it by their values - the five attributes rise
	///         strictly with level - so <c>level = NNN + rank-inside-the-block</c>.
	///     </para>
	///     <para>
	///         ⚠️ <b><c>id = anchor + (level - 1)</c> is WRONG</b>, which is what the first cut of this module assumed. A
	///         class's 80 ids are consecutive, but the eight blocks sit inside that range in an arbitrary per-class ORDER:
	///         the Ranger's run is <c>[31-40][41-50][11-20][71-80][61-70][51-60][21-30][1-10]</c>. That is why its levels
	///         appeared to stop dead at 10 (the level-1 block is LAST) and why the Priest of Mitra was importing level
	///         61-70's values as levels 11-20. Nothing about the id ordering is load-bearing now.
	///     </para>
	///     <para>
	///         The block name also settles the old "the name is not the level" note: it is not the level, it is the level
	///         DECADE - three consecutive Assassin templates all reading "Class Feat 021" are levels 21, 22 and 23.
	///     </para>
	///     <see cref="ClassAnchors" /> - the level-1 template each class was OBSERVED loading in-client - is kept purely as
	///     a cross-check: the decode must place it at level 1. See docs/subsystems/player-stats.md.
	/// </remarks>
	public sealed partial class ClassAttributeBaseModule : IImportModule
	{
		/// <summary>
		///     Every template a character of a KNOWN class and level was observed loading in-client (2026-07-26), with the
		///     <c>GameCode/ItemData</c> + <c>ClientInterface</c> debug channels on. Nothing is located from these - the
		///     decode is name-driven - but the import fails loudly if it cannot reproduce every one of them.
		///     <para>
		///         ⚠️ These are the OBSERVATIONS ONLY. An earlier cut of this module also carried a level-1 id for the
		///         Herald of Xotli, the Necromancer and the Demonologist, but those three were BACK-COMPUTED as
		///         <c>observed - (level - 1)</c> under the since-disproved id rule, and all three are wrong: the arithmetic
		///         silently absorbed the unreadable ids in between (the Herald's by 2) or crossed into another block
		///         entirely (the Necromancer's landed on level 15). Do not re-derive an observation - re-observe it.
		///     </para>
		/// </summary>
		public static readonly IReadOnlyList<(Class Class, uint Level, uint TemplateId)> ClientObservations =
		[
			(Class.Barbarian, 1, 1083955),
			(Class.Guardian, 1, 871090),
			(Class.Conqueror, 1, 873357),
			(Class.PriestOfMitra, 1, 3192040),
			(Class.TempestOfSet, 1, 3067820),
			(Class.BearShaman, 1, 1102716),
			(Class.DarkTemplar, 1, 793759),
			(Class.Assassin, 1, 3074028),
			(Class.Assassin, 23, 3074050),
			(Class.Ranger, 1, 3115334),
			(Class.HeraldOfXotli, 24, 3130655),
			(Class.Necromancer, 80, 1173863),
			(Class.Demonologist, 80, 1126759)
		];

		/// <summary>
		///     The class name each template writes into itself, which is not always the class's current name - the Tempest
		///     of Set's templates are still filed under <b>Stormcaller</b>, the class's launch-era name.
		/// </summary>
		public static readonly IReadOnlyDictionary<string, Class> TemplateClassNames = new Dictionary<string, Class>(StringComparer.Ordinal)
		{
			["Barbarian"] = Class.Barbarian,
			["Guardian"] = Class.Guardian,
			["Conqueror"] = Class.Conqueror,
			["Priest of Mitra"] = Class.PriestOfMitra,
			["Stormcaller"] = Class.TempestOfSet,
			["Bear Shaman"] = Class.BearShaman,
			["Dark Templar"] = Class.DarkTemplar,
			["Assassin"] = Class.Assassin,
			["Ranger"] = Class.Ranger,
			["Necromancer"] = Class.Necromancer,
			["Herald of Xotli"] = Class.HeraldOfXotli,
			["Demonologist"] = Class.Demonologist
		};

		public const uint MaxLevel = 80;
		private const int BlockSize = 10;

		// The stats a base row is allowed to carry. The five attributes and MaxHealth are on every class; 0x0118 (Non
		// Combat Mana Regen) only on the casters, and it is what makes their regen base 2200 rather than the 2000 the
		// client seeds (#442). Anything else in a template is a false-positive signature hit and is dropped.
		private static readonly HashSet<CharacterStatId> Kept =
		[
			CharacterStatId.Strength, CharacterStatId.Constitution, CharacterStatId.Intelligence, CharacterStatId.Wisdom, CharacterStatId.Dexterity,
			CharacterStatId.HealthMax, CharacterStatId.NonCombatManaRegen
		];

		private static readonly CharacterStatId[] Attributes =
			[CharacterStatId.Strength, CharacterStatId.Constitution, CharacterStatId.Intelligence, CharacterStatId.Wisdom, CharacterStatId.Dexterity];

		// The client seeds each attribute 804/806/808/810/814 alongside a parallel ODD-id twin (805/807/809/811/815, plus
		// the 812/813 pair) which it sets to 100 - a percentage, not a score (#444). A class-BASE template never touches
		// the twins; a template that does belongs to some other feat series that happens to share the class name, and its
		// attribute amounts are single digits rather than the base's multiples of ten. Rejecting on the twins is what
		// keeps those out of the level blocks - 5 strays in the Ranger's block 001, 10 in the Herald of Xotli's block 021.
		private static readonly HashSet<uint> AttributeTwins = [805, 807, 809, 811, 812, 813, 815];

		/// <summary>One level's template: its RDB id, the block its name declares, and its stat records.</summary>
		public readonly record struct Template(uint Id, string ClassName, uint BlockFirstLevel, List<ItemTemplateStatModifiers.Modifier> Mods)
		{
			/// <summary>Orders the members of a block: the five attributes rise strictly with level.</summary>
			public long AttributeSum => Mods.Where(m => Attributes.Contains((CharacterStatId)m.StatId)).Sum(m => (long)m.WireAmount);
		}

		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert, WriteMode.Overwrite };

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Class/level attribute + health base from the RDB class-feat template blocks (#444).";
		public string Name => "class-attributes";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!, ctx.RdbProxyBase);
			var byClass = ScanRuns(rdb).ToDictionary(kv => kv.Key, kv => LevelsOf(kv.Value));
			var rows = new List<ClassAttributeBase>();
			var problems = new List<string>();

			foreach (var (cls, byLevel) in byClass)
			{
				foreach (var (level, t) in byLevel)
				foreach (var mod in t.Mods)
					if (Kept.Contains((CharacterStatId)mod.StatId))
						rows.Add(new ClassAttributeBase { Class = cls, Level = level, StatId = (CharacterStatId)mod.StatId, Amount = (int)mod.WireAmount });

				if (byLevel.Count != MaxLevel)
					problems.Add($"{cls} {byLevel.Count}/{MaxLevel} levels");
			}

			foreach (var cls in TemplateClassNames.Values.Where(c => !byClass.ContainsKey(c)))
				problems.Add($"{cls} no run found");

			// The client observations are the acceptance test, not an input: whatever the scan decides, every template a
			// character of a known class and level was seen loading has to come out at that level.
			foreach (var (cls, level, templateId) in ClientObservations)
			{
				var got = byClass.GetValueOrDefault(cls)?.GetValueOrDefault(level).Id;
				if (got != templateId)
					problems.Add($"{cls} level {level} resolved to {got?.ToString() ?? "nothing"}, client observed {templateId}");
			}

			// Delete + reinsert: the natural (Class, Level, StatId) key makes this a clean rebuild with no churn.
			await ctx.Db.ClassAttributeBases.ExecuteDeleteAsync();
			ctx.Db.ClassAttributeBases.AddRange(rows.DistinctBy(r => (r.Class, r.Level, r.StatId)));
			await ctx.Db.SaveChangesAsync();

			foreach (var cls in TemplateClassNames.Values.OrderBy(c => c.ToString(), StringComparer.Ordinal))
			{
				var n = byClass.GetValueOrDefault(cls)?.Count ?? 0;
				ctx.Out.WriteLine($"  {cls,-16} {n,3}/{MaxLevel} levels{(n < MaxLevel ? "   ⚠ INCOMPLETE (#444)" : "")}");
			}

			return new ImportResult($"{rows.Count} base rows over {byClass.Count} classes" +
			                        (problems.Count > 0
				                        ? $"; ⚠ {string.Join("; ", problems)}"
				                        : $"; all {MaxLevel} levels of each, and all {ClientObservations.Count} client observations reproduced"));
		}

		/// <summary>
		///     Every class-feat template in the RDB, grouped by class - the RAW candidate set, strays included. Use
		///     <see cref="LevelsOf" /> to turn one class's candidates into its level map.
		/// </summary>
		public static Dictionary<Class, List<Template>> ScanRuns(RdbReader rdb)
		{
			var byClass = new Dictionary<Class, List<Template>>();
			foreach (var e in rdb.EntriesOfType(1000020).Where(rdb.CanRead))
			{
				if (!TryReadTemplate(rdb, e, out var t) || !TemplateClassNames.TryGetValue(t.ClassName, out var cls))
					continue;

				(byClass.TryGetValue(cls, out var list)
					? list
					: byClass[cls] = []).Add(t);
			}

			return byClass;
		}

		/// <summary>
		///     level -> template, from the block each template names plus its rank inside that block. Both halves are
		///     needed: the name gives the decade, the values give the position within it.
		/// </summary>
		public static Dictionary<uint, Template> LevelsOf(IEnumerable<Template> run)
		{
			var levels = new Dictionary<uint, Template>();
			foreach (var block in run.GroupBy(t => t.BlockFirstLevel))
			{
				var ordered = block.OrderBy(t => t.AttributeSum).ThenBy(t => t.Id).ToList();
				for (var i = 0; i < ordered.Count && i < BlockSize; i++)
					levels[block.Key + (uint)i] = ordered[i];
			}

			return levels;
		}

		// A class-base template is a PLAINTEXT type-1000020 entry (the canonical 12-byte RDB-Id header survives only when
		// the entry is not TEA-encrypted, so it doubles as the "is this readable at all" check) that names itself
		// "<Class> Class Feat <NNN>" AND carries all five attributes at sane amounts. Both halves are load-bearing: the
		// name alone also matches other feat series whose records parse as garbage in the hundreds of millions, and the
		// records alone cannot say which class or which decade.
		private static bool TryReadTemplate(RdbReader rdb, RdbReader.Entry entry, out Template template)
		{
			template = default;
			byte[] blob;
			try
			{
				blob = rdb.ReadDecompressed(entry);
			}
			catch
			{
				return false;
			}

			if (blob.Length < 24 || blob.ReadU32Le(0) != 1000020 ||
			    blob.ReadU32Le(4) != entry.Id)
				return false;

			var name = AsciiRuns(blob).Select(r => ClassFeatName().Match(r)).FirstOrDefault(m => m.Success);
			if (name == null || !uint.TryParse(name.Groups[2].Value, out var block))
				return false;

			var mods = ItemTemplateStatModifiers.Parse(blob.AsSpan(12)).Where(m => m.WireAmount < 100_000).ToList();
			if (!Attributes.All(a => mods.Any(m => m.StatId == (uint)a)) || mods.Any(m => AttributeTwins.Contains(m.StatId)))
				return false;

			template = new Template(entry.Id, name.Groups[1].Value, block, mods);
			return true;
		}

		// The name is embedded mid-blob and is NOT null-terminated on every class - the Bear Shaman's and Tempest of
		// Set's run straight into the following "No description given." string - so the match is anchored at the start of
		// a printable run and left open at the end.
		[GeneratedRegex(@"^(.+?) Class Feat (\d+)")]
		private static partial Regex ClassFeatName();

		// Printable ASCII runs of 4+ chars in a blob - the class-feat name is one of them. Stays private: ReportCli
		// had a byte-identical copy, but it was DEAD (left behind when this scan moved out of the CLI into this
		// module), so deleting it leaves one caller - and a single-use helper belongs next to its caller.
		private static IEnumerable<string> AsciiRuns(byte[] blob)
		{
			var sb = new StringBuilder();
			foreach (var b in blob)
			{
				if (b is >= 32 and < 127)
				{
					sb.Append((char)b);
					continue;
				}

				if (sb.Length >= 4)
					yield return sb.ToString();

				sb.Clear();
			}

			if (sb.Length >= 4)
				yield return sb.ToString();
		}
	}
}

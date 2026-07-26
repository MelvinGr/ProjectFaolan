using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
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
	///     named data tables either. It lives in RDB type <b>1000020</b>, the ITEM-template type, as a run of PLAINTEXT
	///     templates - one per class per level - whose stat records are read by the ordinary item parser
	///     (<see cref="ItemTemplateStatModifiers" />). So:
	///     <code>
	///     templateId = ClassBases[class] + (level - 1)
	///     </code>
	///     ⚠️ <b>The per-class base id cannot be derived; every one below is a CLIENT OBSERVATION.</b> A level-1 character
	///     of the class was brought in-game with the <c>GameCode/ItemData</c> + <c>ClientInterface</c> debug channels on,
	///     and the template the client loaded before writing the five attributes IS that class's base. Two cheaper rules
	///     were tried and both fail: the lowest id of the name-matched run is wrong for 7 of the 12 (Ranger by -70, Priest
	///     of Mitra by -60, and in both directions), and the name is neither unique nor level-aligned (the Demonologist has
	///     three templates called "Class Feat 001"; the Necromancer's base is called "Class Feat 011"). Re-derive one only
	///     by re-observing it - see docs/subsystems/player-stats.md.
	/// </remarks>
	public sealed class ClassAttributeBaseModule : IImportModule
	{
		// Level 1 -> ClassBases[c]; level 80 -> ClassBases[c] + 79. Every id client-observed, 2026-07-26.
		private static readonly IReadOnlyDictionary<Class, uint> ClassBases = new Dictionary<Class, uint>
		{
			[Class.Barbarian] = 1083955,
			[Class.Guardian] = 871090,
			[Class.Conqueror] = 873357,
			[Class.PriestOfMitra] = 3192040,
			[Class.TempestOfSet] = 3067820, // the RDB still calls it "Stormcaller", the class's old name
			[Class.BearShaman] = 1102716,
			[Class.DarkTemplar] = 793759,
			[Class.Assassin] = 3074028,
			[Class.Ranger] = 3115334,
			[Class.Necromancer] = 1173784,
			[Class.HeraldOfXotli] = 3130632,
			[Class.Demonologist] = 1126680
		};

		private const uint MaxLevel = 80;

		// The stats a base row is allowed to carry. The five attributes and MaxHealth are on every class; 0x0118 (Non
		// Combat Mana Regen) only on the casters, and it is what makes their regen base 2200 rather than the 2000 the
		// client seeds (#442). Anything else in a template is a false-positive signature hit and is dropped.
		private static readonly HashSet<CharacterStatId> Kept =
		[
			CharacterStatId.Strength, CharacterStatId.Constitution, CharacterStatId.Intelligence, CharacterStatId.Wisdom, CharacterStatId.Dexterity,
			CharacterStatId.HealthMax, CharacterStatId.NonCombatManaRegen
		];

		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert, WriteMode.Overwrite };

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Class/level attribute + health base from the RDB class-feat template runs (#444).";
		public string Name => "class-attributes";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!, ctx.RdbProxyBase);
			var rows = new List<ClassAttributeBase>();
			int missing = 0, notPlaintext = 0;

			// Index the type ONCE - it holds ~98k entries, so a per-level scan would be 12 x 80 passes over all of them.
			var byId = rdb.EntriesOfType(1000020).GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

			foreach (var (cls, baseId) in ClassBases)
			for (uint level = 1; level <= MaxLevel; level++)
			{
				if (!byId.TryGetValue(baseId + level - 1, out var entry) || !rdb.CanRead(entry))
				{
					missing++;
					continue;
				}

				var blob = rdb.ReadDecompressed(entry);
				// These templates are PLAINTEXT - the canonical RDB-Id header survives only when the entry is not
				// TEA-encrypted, so this doubles as the "did we land on a real class-feat template" check.
				if (blob.Length < 24 || BitConverter.ToUInt32(blob, 0) != 1000020 || BitConverter.ToUInt32(blob, 4) != entry.Id)
				{
					notPlaintext++;
					continue;
				}

				foreach (var mod in ItemTemplateStatModifiers.Parse(blob.AsSpan(12)))
				{
					if (Kept.Contains((CharacterStatId)mod.StatId) && mod.WireAmount < 1_000_000)
						rows.Add(new ClassAttributeBase { Class = cls, Level = level, StatId = (CharacterStatId)mod.StatId, Amount = (int)mod.WireAmount });
				}
			}

			// Delete + reinsert: the natural (Class, Level, StatId) key makes this a clean rebuild with no churn.
			await ctx.Db.ClassAttributeBases.ExecuteDeleteAsync();
			ctx.Db.ClassAttributeBases.AddRange(rows.DistinctBy(r => (r.Class, r.Level, r.StatId)));
			await ctx.Db.SaveChangesAsync();

			// ⚠️ Report coverage PER CLASS. The run is contiguous from the anchor for most classes but NOT all - the
			// Ranger's stops dead at level 10 (3115344 simply does not exist) and the Priest of Mitra's at 20 - so those
			// classes' higher levels live in some other id block we have not located. Printing it per class is what keeps
			// that visible instead of it hiding inside a single "175 missing" total and shipping as silently wrong data.
			var covered = rows.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Select(r => r.Level).Distinct().Count());
			foreach (var (cls, _) in ClassBases.OrderBy(kv => kv.Key.ToString()))
			{
				var n = covered.GetValueOrDefault(cls);
				ctx.Out.WriteLine($"  {cls,-16} {n,3}/{MaxLevel} levels{(n < MaxLevel ? "   ⚠ INCOMPLETE - higher levels not located (#444)" : "")}");
			}

			var incomplete = ClassBases.Count(kv => covered.GetValueOrDefault(kv.Key) < MaxLevel);
			return new ImportResult($"{rows.Count} base rows; {ClassBases.Count - incomplete}/{ClassBases.Count} classes fully covered" +
			                        (incomplete > 0
				                        ? $", {incomplete} INCOMPLETE ({missing} ids absent, {notPlaintext} not plaintext)"
				                        : ""));
		}
	}
}

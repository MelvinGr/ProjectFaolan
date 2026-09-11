using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     What a combo's gating feat says about it.
	/// </summary>
	/// <remarks>
	///     `Combos.FeatId` is not a `Feats.Id` - it is the wire feat id, so it resolves through `FeatIdMaps.WireFeatId
	///     -> TemplateId -> Feats.Id`. No `combo_npc_` / `monster_` / `generic_` NPC combo carries one, so carrying a
	///     gating feat is a mechanical discriminator for "this is a player combo".
	///     Two things are readable off the feat, and they are independent.
	///     Class - the client names it in the feat's own name: `<Class> Class Feat NNN` names that one class
	///     (`Guardian Class Feat 041`); `<Archetype>: Class Specific N` names the archetype's three classes
	///     (`Rogue: Class Specific 1`); a bare `<Class>` names that one class, for the handful named after the class
	///     outright (`Ranger`). Where both the feat and the catalog name prefix speak they agree; the feat is narrower
	///     than the prefix on a few (`Priest_…_2hb` -> Bear Shaman) and wider on none.
	///     Level - only where every levelled combo the feat gates carries the same level. `Combos.Level <= 1` is the
	///     catalog's sentinel for "not stated", so a feat that gates one levelled combo and several sentinel ones lends
	///     its level to them (`Rogue: Class Specific 1` gates `Rogue_05_…` combos at level 5, so the sentinel-level
	///     `SneakAttack_*` combos it gates are level 5). Most feats gate a range of levels, and those lend nothing.
	/// </remarks>
	public sealed class FeatGating
	{
		// `Halberdier_50_…` is gated by `Guardian Class Feat 041`; the trailing number is the feat's own index within
		// its class tree and says nothing about the combo, so it is matched and discarded.
		private static readonly Regex ClassFeat = new Regex(@"^(?<class>.+?)\s+Class Feat\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex ArchetypeFeat = new Regex(@"^(?<archetype>.+?):\s*Class Specific\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private readonly Dictionary<uint, ConanClass[]> _classes = [];
		private readonly Dictionary<uint, byte> _levels = [];

		public static async Task<FeatGating> LoadAsync(IDatabaseContext db)
		{
			var featNameOfWireId = await db
				.FeatIdMaps.Join(db.Feats, m => m.TemplateId, f => f.Id, (m, f) => new
				{
					m.WireFeatId,
					f.Name
				})
				.ToDictionaryAsync(x => x.WireFeatId, x => x.Name);

			var gating = new FeatGating();
			foreach (var (wireId, name) in featNameOfWireId)
				if (ClassesNamedBy(name) is { Length: > 0 } classes)
					gating._classes[wireId] = classes;

			// The level a feat lends: the single level shared by every non-sentinel combo it gates.
			var levelled = await db.Combos.Where(c => c.FeatId != null && c.Level > 1).Select(c => new
			{
				FeatId = c.FeatId!.Value,
				c.Level
			}).ToListAsync();

			foreach (var group in levelled.GroupBy(x => x.FeatId))
			{
				var distinct = group.Select(x => x.Level).Distinct().ToList();
				if (distinct.Count == 1)
					gating._levels[group.Key] = distinct[0];
			}

			return gating;
		}

		/// <summary>
		///     The classes the feat gating this combo names, or empty when it names none (or the combo has no feat).
		/// </summary>
		public IReadOnlyList<ConanClass> ClassesOf(uint? wireFeatId)
		{
			return wireFeatId is { } id && _classes.TryGetValue(id, out var classes) ? classes : [];
		}

		/// <summary>
		///     The level the gating feat lends a combo whose own level is the catalog sentinel, or null.
		/// </summary>
		public byte? LevelOf(uint? wireFeatId)
		{
			return wireFeatId is { } id && _levels.TryGetValue(id, out var level) ? level : null;
		}

		private static ConanClass[] ClassesNamedBy(string? featName)
		{
			if (string.IsNullOrWhiteSpace(featName))
				return [];

			if (ClassFeat.Match(featName) is { Success: true } m && ConanClasses.FromName(m.Groups["class"].Value) is { } cls)
				return [cls];

			if (ArchetypeFeat.Match(featName) is { Success: true } a
			    && Enum.TryParse<ConanArchetype>(a.Groups["archetype"].Value.Trim(), true, out var archetype)
			    && ConanArchetypes.ClassesOf(archetype) is { Count: > 0 } members)
				return [.. members];

			// A feat named after the class outright - `Ranger`, which gates `SneakAttack_bow` / `_crossbow`.
			return ConanClasses.FromName(featName) is { } bare ? [bare] : [];
		}
	}
}

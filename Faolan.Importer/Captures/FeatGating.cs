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
	// What a combo's GATING FEAT says about it (#581).
	//
	// `Combos.FeatId` is not a `Feats.Id` - it is the WIRE feat id, so it resolves through `FeatIdMaps.WireFeatId ->
	// TemplateId -> Feats.Id`. 482 of the 744 catalogued combos carry one, and **none of the 97 `combo_npc_` /
	// `monster_` / `generic_` NPC combos does**, which is worth knowing on its own: carrying a gating feat is a
	// mechanical discriminator for "this is a PLAYER combo".
	//
	// Two things are readable off the feat, and they are independent:
	//
	//   CLASS - the client names it in the feat's own name, in two shapes:
	//     `<Class> Class Feat NNN`      -> that one class   (`Guardian Class Feat 041`, `Ranger Class Feat 001`)
	//     `<Archetype>: Class Specific N` -> the archetype's three classes (`Rogue: Class Specific 1`)
	//     `<Class>`                     -> that one class, for the handful named after the class outright (`Ranger`)
	//   ⭐ Validated against the name-prefix rule it now leads: over every combo where BOTH speak, 134 agree and
	//   ZERO conflict. It is narrower than the prefix on three (`Priest_…_2hb` -> Bear Shaman) and wider on none.
	//
	//   LEVEL - only where every LEVELLED combo the feat gates carries the same level. `Combos.Level <= 1` is the
	//   catalog's sentinel for "not stated", so a feat that gates one levelled combo and several sentinel ones lends
	//   its level to them. `Rogue: Class Specific 1` gates three `Rogue_05_…` combos at level 5 and the seven
	//   `SneakAttack_*` at the sentinel, so the seven are level 5. ⚠️ Most feats gate a RANGE (`Ranger Class Feat
	//   001` covers 5/7/10), and those lend nothing - one feat, several levels, no answer.
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

		// The classes the feat gating this combo names, or empty when it names none (or the combo has no feat).
		public IReadOnlyList<ConanClass> ClassesOf(uint? wireFeatId)
		{
			return wireFeatId is { } id && _classes.TryGetValue(id, out var classes) ? classes : [];
		}

		// The level the gating feat lends a combo whose own level is the catalog sentinel, or null.
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

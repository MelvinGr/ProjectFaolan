using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report spell-grant-check`: does the level path offer a captured character the spellbook retail gave them?
	//
	// The oracle is `CharacterSpells` - the known-spell lists harvested from the captures, of which two are level-80
	// (a Necromancer's 109 and a Demonologist's 127). For each of them this runs the SHIPPING queries
	// (`GetStartingSpells`, the relog re-grant, and `GetStartingBarSpells`) and prints the two errors separately,
	// because only one of them can only be a defect:
	//
	//   EXTRA   - a spell the path offers that the character does not know. Retail is the leading source, so an extra
	//             is wrong; but the population moves whenever captures or the catalog grow, so it is PRINTED.
	//   MISSING - a spell the character demonstrably knows that the path would not restore. Split FIVE ways, because
	//             four of them are another mechanism and only the last is a defect:
	//               * never on offer      - not in `ClassSpells` for the class, or `Level 0`, or a combo. Today this
	//                                       is entirely `Level 0`: mounts, pets, `Dance:`/`Social Ability:` emotes,
	//                                       `Path:` travel unlocks and veteran rewards - earned or bought, never
	//                                       leveled into, and replayed on login from the character's OWN rows
	//                                       (`GrantCharacterSpells`) rather than re-derived from the class.
	//               * held below level    - the catalog gates it above the character's own level, so retail granted
	//                                       it earlier than the client's record says. `ClassSpell.GrantLevel` is
	//                                       where that gets written, but only a capture of the GRANT states it; a
	//                                       known-spell list merely bounds it, so this is reported, not written.
	//               * no teaching feat    - zero `FeatSpellGrants` rows, so the feat gate cannot pass it. A gap in
	//                                       the feat harvest rather than in the gate.
	//               * bought in the TREE  - taught only by a non-`Feat.AutomaticUnlockCategory` feat, i.e. a
	//                                       `Spell - <name>` the player spent a point on. `FeatService` hands these
	//                                       over when the feat is trained.
	//               * DROPPED             - none of the above. Nothing explains it but a bug.
	//
	// ⭐ **Why a report and not a test** (Gitea #729): both sides are shipped DATA - importer OUTPUT (`ClassSpells` +
	// `Spells.Level` + `FeatSpellGrants`) against importer INPUT (the captured known-spell lists). It also physically
	// cannot be a test: `faolan.sql` excludes every `Character*` table as per-developer play state, so the suite's
	// fixture holds ZERO `CharacterSpells` rows and any such gate there passes vacuously.
	//
	// It is currently **EXTRA 0 / DROPPED 0** on every captured character, at 97 of the level-80 Necromancer's 109
	// and 96 of the level-80 Demonologist's 127, with every one of the rest attributed to one of the four
	// mechanisms above.
	public static partial class ReportCli
	{
		private static async Task<int> SpellGrantCheck(string? nameFilter)
		{
			await using var db = OpenDb();
			var repo = new DatabaseRepository(new ReportContextFactory());

			var characters = await db.Characters.AsNoTracking()
				.Select(c => new
				{
					c.Id,
					c.Name,
					c.Class,
					c.Level
				})
				.OrderByDescending(c => c.Level).ToListAsync();

			var comboActions = (await db.Combos.AsNoTracking().Select(c => c.GameActionId).ToListAsync()).ToHashSet();
			var levels = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Level);
			var names = await db.Spells.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name);

			// The feat TREE half of the catalog: a spell no AUTOMATIC feat teaches is not the level path's to grant,
			// it is bought with a feat point and handed over by `FeatService` when that feat is trained. Counted
			// apart for exactly that reason - it is a spell the character has by a DIFFERENT mechanism, not one the
			// level path lost.
			var autoTaught = (await db.FeatSpellGrants.AsNoTracking()
				.Where(g => g.Feat.TemplateCategory == Feat.AutomaticUnlockCategory)
				.Select(g => g.SpellId).Distinct().ToListAsync()).ToHashSet();
			var anyTaught = (await db.FeatSpellGrants.AsNoTracking().Select(g => g.SpellId).Distinct().ToListAsync()).ToHashSet();

			var failures = 0;
			var checkedCharacters = 0;
			failures += await CapturedRecordExclusions(db);

			foreach (var character in characters)
			{
				var known = (await db.CharacterSpells.AsNoTracking().Where(cs => cs.CharacterId == character.Id).Select(cs => cs.SpellId).ToListAsync()).ToHashSet();
				if (known.Count == 0)
					continue;

				checkedCharacters++;

				// The class's own catalog rows, with the GATE level the two grant paths read - `ClassSpell.GrantLevel`
				// where a capture caught the grant and `Spells.Level` everywhere else. Absent from this map is a
				// harvest gap; present in it but above the character's own level is a level-DATA disagreement, which
				// is a different thing from the gate dropping it.
				var catalogGate = await db.ClassSpells.AsNoTracking()
					.Where(cs => cs.Class == 0 || cs.Class == (ConanClass)character.Class)
					.Join(db.Spells.AsNoTracking(), cs => cs.SpellId, sp => sp.Id, (cs, sp) => new
					{
						cs.SpellId,
						Gate = (uint)(cs.GrantLevel ?? sp.Level)
					})
					.ToDictionaryAsync(x => x.SpellId, x => x.Gate);
				var inCatalog = catalogGate.Keys.ToHashSet();

				var offered = (await repo.GetStartingSpells(Math.Max(character.Level, (ushort)1), (ConanClass)character.Class)).ToHashSet();
				var bar = await repo.GetStartingBarSpells(character.Level, (ConanClass)character.Class);

				var missing = known.Where(id => !offered.Contains(id)).ToList();
				var offCatalog = missing.Where(id => !inCatalog.Contains(id) || levels.GetValueOrDefault(id) == 0 || comboActions.Contains(id)).ToList();
				var rest = missing.Except(offCatalog).ToList();
				// Held BELOW the level the catalog gates it at - retail granted it earlier than the client's own
				// record says. That is what `ClassSpell.GrantLevel` records where a capture catches the grant itself;
				// a known-spell list only bounds it, so it is reported rather than written.
				var earlyHeld = rest.Where(id => catalogGate.GetValueOrDefault(id) > character.Level).ToList();
				rest = rest.Except(earlyHeld).ToList();
				var noFeat = rest.Where(id => !anyTaught.Contains(id)).ToList();
				var fromTree = rest.Except(noFeat).Where(id => !autoTaught.Contains(id)).ToList();
				var dropped = rest.Except(noFeat).Except(fromTree).ToList();
				var extra = offered.Where(id => !known.Contains(id)).ToList();

				Console.WriteLine($"{character.Name} (class {(ConanClass)character.Class}, level {character.Level}): knows {known.Count}, path offers {offered.Count}.");
				Console.WriteLine($"  matched {known.Count - missing.Count}   EXTRA {extra.Count}   missing {missing.Count} = {offCatalog.Count} never on offer"
				                  + $" + {earlyHeld.Count} held below their stated level + {noFeat.Count} no teaching feat"
				                  + $" + {fromTree.Count} bought in the feat TREE + {dropped.Count} DROPPED");

				// `--report-arg <character name>` names both populations instead of counting them - which is the only
				// way an EXTRA becomes actionable, since what it means is decided one spell at a time.
				if (!string.IsNullOrWhiteSpace(nameFilter) && character.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) == true)
				{
					foreach (var (label, ids) in new[] { ("EXTRA (offered, not known)", extra), ("MISSING, never on offer", offCatalog), ("MISSING, bought in the feat TREE", fromTree), ("MISSING, held below their stated level", earlyHeld), ("MISSING, no teaching feat", noFeat) })
					{
						Console.WriteLine($"  {label}:");
						foreach (var id in ids.OrderBy(id => levels.GetValueOrDefault(id)).ThenBy(id => names.GetValueOrDefault(id, "")))
							Console.WriteLine($"    level {levels.GetValueOrDefault(id),3}  {names.GetValueOrDefault(id, "?"),-38} {id,-9}"
							                  + $" {(inCatalog.Contains(id) ? "class" : "     ")}");
					}
				}

				var offBar = bar.Where(id => !offered.Contains(id)).ToList();
				if (offBar.Count > 0)
				{
					Console.WriteLine($"  ⚠️ the default bar seeds {offBar.Count} spell(s) the book does not hold: {Describe(offBar, names)}");
					failures++;
				}

				if (dropped.Count > 0)
				{
					Console.WriteLine($"  ⚠️ DROPPED - in the class catalog, carries a level, and still not offered: {Describe(dropped, names)}");
					failures++;
				}
			}

			// ⚠️ On characters that CARRY a known-spell list, not on characters at all (#849). A database full of
			// freshly-created play characters has plenty of `Characters` rows and no oracle in any of them, which is
			// the same vacuous pass wearing a different face - the loop above `continue`s straight past every one.
			if (checkedCharacters == 0)
				Console.WriteLine($"no character in this database carries a known-spell list ({characters.Count} characters, 0 with an oracle)"
				                  + " - the check is vacuous, so this is a failure, not a pass.");

			if (failures == 0 && checkedCharacters > 0)
				Console.WriteLine("✅ no captured character loses a spell its own class catalog offers, and no bar slot points outside the book.");

			return failures == 0 && checkedCharacters > 0 ? 0 : 1;
		}

		// The three EXCLUSION rules, checked against the records retail actually shipped (Gitea #849). Each says the
		// same kind of thing - "this is not a book/feat entry, so no captured character may hold one" - and each was
		// a TEST that could not fail: they intersected against `CharacterSpells` / `CharacterFeats`, and `faolan.sql`
		// drops every `Character*` table as per-developer play state, so the suite's fixture holds zero rows on the
		// left-hand side. Three green gates over an empty set.
		//
		// They belong here for the reason #729 gives - both sides are shipped data, importer OUTPUT (`Combos`,
		// `Spells.EffectSpell`, `FeatIdMap`) against importer INPUT (the harvested known-spell and feat lists) - and
		// here they run against the real `faolan.db`, where the oracle exists: 249 known-spell entries across four
		// captured characters, two of them level-80.
		//
		// ⚠️ These are ASSERTED rather than printed, unlike the per-character counts below. A count moves whenever
		// captures grow; a combo appearing in a retail spellbook does not move, it REFUTES the exclusion - retail
		// would be saying the thing we exclude is a book entry after all. That can only be a defect in the rule.
		private static async Task<int> CapturedRecordExclusions(Faolan.Core.Database.DatabaseContext db)
		{
			var knownSpells = (await db.CharacterSpells.AsNoTracking().Select(s => s.SpellId).Distinct().ToListAsync()).ToHashSet();
			var carriedFeats = await db.CharacterFeats.AsNoTracking().Select(f => f.FeatId).Distinct().ToListAsync();

			// ⭐ The guard that makes this impossible to go vacuous again. An empty oracle is a FAILURE, not a pass -
			// which is the whole lesson of #849, and the reason the emptiness went unnoticed for as long as it did.
			if (knownSpells.Count == 0 || carriedFeats.Count == 0)
			{
				Console.WriteLine($"⚠️ captured-record exclusions are VACUOUS: {knownSpells.Count} known spells, {carriedFeats.Count} carried feats."
				                  + " Import the characters (`--only characters`) - a check over an empty oracle is a failure, not a pass.");
				return 1;
			}

			var failures = 0;

			// #298: a COMBO is not a spellbook spell. `Combos.GameActionId` IS a `Spells.Id`, and combos are
			// attributed to their class exactly like spells because a melee combo's GameAction carries the same
			// class-encoded stacking line - correct as MEMBERSHIP and wrong as a spellbook.
			var comboActions = (await db.Combos.AsNoTracking().Where(c => c.GameActionId != 0).Select(c => c.GameActionId).ToListAsync()).ToHashSet();
			var comboLeak = knownSpells.Where(comboActions.Contains).ToList();

			// #847: nor is an EFFECT. The DoT a spell applies carries its caster's stacking line, so it lands in
			// `ClassSpells` by the same route. The referrer must itself be a real leveled entry - `Flesh to Worms`
			// is named as the effect of a Level-0 `Plague Swarm`, and "anything some spell points at" would delete
			// the very spell the level-2 ding exists to teach.
			var book = (await db.ClassSpells.AsNoTracking().Select(cs => cs.SpellId).Distinct().ToListAsync()).ToHashSet();
			var effects = (await db.Spells.AsNoTracking().Where(s => s.EffectSpell != 0 && s.Level > 0).Select(s => new
			{
				s.Id,
				s.EffectSpell
			}).ToListAsync()).Where(a => book.Contains(a.Id)).Select(a => a.EffectSpell).ToHashSet();
			var effectLeak = knownSpells.Where(effects.Contains).ToList();

			// The feat-side twin: a PROGRESSION TRACK is not a trainable feat. Its low digits run past the client's
			// own rank ceiling, so they index a family rather than name a rank.
			var tracks = (await db.FeatIdMaps.AsNoTracking()
				.GroupBy(m => m.BaseIndex)
				.Where(g => g.Max(m => m.Rank) >= FeatIdMap.MaxRank)
				.Select(g => g.Key)
				.ToListAsync()).ToHashSet();
			var trackWireIds = (await db.FeatIdMaps.AsNoTracking().Where(m => tracks.Contains(m.BaseIndex)).Select(m => m.WireFeatId).ToListAsync()).ToHashSet();
			var trackLeak = carriedFeats.Where(trackWireIds.Contains).ToList();

			Console.WriteLine($"captured-record exclusions over {knownSpells.Count} known spells / {carriedFeats.Count} carried feats"
			                  + $" ({comboActions.Count} combo actions, {effects.Count} book-spell effects, {tracks.Count} progression tracks):");

			foreach (var (label, leaked) in new[] { ("a combo's GameAction", comboLeak), ("a book spell's effect", effectLeak) })
			{
				if (leaked.Count == 0)
					continue;

				Console.WriteLine($"  ⚠️ captured spellbook entries that are {label}: {string.Join(", ", leaked)}");
				failures++;
			}

			if (trackLeak.Count > 0)
			{
				Console.WriteLine($"  ⚠️ captured feat lists carrying a progression track: {string.Join(", ", trackLeak)}");
				failures++;
			}

			if (failures == 0)
				Console.WriteLine("  ✅ no captured spellbook holds a combo or an effect, and no captured feat list holds a progression track.");

			return failures;
		}

		private static string Describe(IEnumerable<uint> ids, Dictionary<uint, string> names)
		{
			return string.Join(", ", ids.Take(15).Select(id => $"{names.GetValueOrDefault(id, "?")} ({id})"));
		}

		// The repository takes a context FACTORY and disposes what it hands back per unit of work, so this must mint a
		// FRESH context each time - handing out one shared instance would leave it disposed after the first query.
		private sealed class ReportContextFactory : IDbContextFactory<Faolan.Core.Database.DatabaseContext>
		{
			public Faolan.Core.Database.DatabaseContext CreateDbContext()
			{
				return OpenDb();
			}

			public Task<Faolan.Core.Database.DatabaseContext> CreateDbContextAsync(System.Threading.CancellationToken ct = default)
			{
				return Task.FromResult(OpenDb());
			}
		}
	}
}

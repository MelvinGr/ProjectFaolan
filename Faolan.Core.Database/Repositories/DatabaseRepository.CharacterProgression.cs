using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// What the character has LEARNED: feats and their graph, spells, skills, abilities, combos, the shortcut bar and
	// the level/experience line. One of the five character slices (Gitea #928 item 9); what those do to the stat
	// sheet is ICharacterStatRepository.
	public interface ICharacterProgressionRepository
	{
		Task<int> AddCharacterFeats(uint characterId, IEnumerable<uint> featIds);
		Task<bool> AddCharacterSpell(uint characterId, uint spellId);

		/// <summary>
		///     Un-learns one spell. False when the character did not know it. The caller is responsible for the
		///     client half (<c>DispatchForgetSpell</c>) - this only touches the row.
		/// </summary>
		Task<bool> RemoveCharacterSpell(uint characterId, uint spellId);

		/// <summary>Un-learns every spell the character knows; returns how many rows went.</summary>
		Task<int> ClearCharacterSpells(uint characterId);
		Task<int> ClearCharacterFeats(uint characterId);
		Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds);
		Task<CharacterAbility[]> GetCharacterAbilities(uint characterId);
		Task<CharacterCombo[]> GetCharacterCombos(uint characterId);
		Task<uint[]> GetCharacterFeats(uint characterId);

		// The WIRE ids of the AUTOMATIC feats - the handed-out ones, which cost no feat points (#850).
		Task<HashSet<uint>> GetAutotrainedFeatWireIds();

		/// <summary>
		///     What each of <paramref name="wireFeatIds" /> costs as an ALTERNATE-ADVANCEMENT PERK for
		///     <paramref name="conanClass" />, and out of which token pool (#150). A feat with no entry here is an
		///     ordinary feat, bought with a feat point.
		/// </summary>
		Task<Dictionary<uint, (TokenPool Pool, uint Cost)>> GetPerkCosts(ConanClass conanClass, IEnumerable<uint> wireFeatIds);

		// The inverse: the TREE spells a set of just-trained feats teaches. See the implementation's remarks.
		Task<uint[]> GetSpellsTaughtByFeats(IEnumerable<uint> wireFeatIds);
		Task<Dictionary<uint, uint[]>> GetFeatPrereqs(IEnumerable<uint> featIds);

		// The whole feat graph's single-prerequisite edges - what the tradeskill ladders are walked out of (#735).
		Task<Dictionary<uint, uint>> GetSingleFeatPrereqs();

		// The WIRE feat id of an RDB feat TEMPLATE id, or null when the template has no wire id at all. The two id
		// spaces are bridged by FeatIdMaps; this is the direction a requirement stated as a template id needs, which
		// is how retail's own `FeatRequired` refusal names a feat (#779).
		Task<uint?> GetWireFeatId(uint templateFeatId);

		// Every wire id a character's spawn scaffold carries in `CharacterSkills`. The TRADESKILL professions ride
		// this list (#735), which is why it is read as ids rather than through the slot it sits in.
		Task<uint[]> GetCharacterSkillIds(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task RemoveCharacterShortcut(uint characterId, uint slot);
		Task SetCharacterProgress(uint characterId, uint experience, ushort level);
		Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId);

		/// <summary>The two ALTERNATE-ADVANCEMENT xp pools (#150), persisted as they move.</summary>
		Task SetAlternateAdvancementPools(uint characterId, uint pvE, uint pvP);

		/// <summary>Every token wallet the character has ever touched. An absent pool is 0/0.</summary>
		Task<CharacterToken[]> GetCharacterTokens(uint characterId);

		/// <summary>
		///     Adds to one wallet's LIFETIME totals and returns what they now read. Both halves move independently -
		///     an award raises <c>gained</c>, a purchase raises <c>spent</c> - because that is what the wire carries.
		/// </summary>
		Task<(ulong Gained, ulong Spent)> AddTokens(uint characterId, TokenPool pool, ulong gained, ulong spent);
	}

	public partial class DatabaseRepository
	{
		// Trains feats onto a character. A feat id is composite (base*100 + rank) and retail keeps exactly ONE id per
		// BASE - the highest rank held. No captured character carries two ranks of one base, so ranking a feat up
		// REPLACES its row; keeping both would put an entry on the wire retail never sends and would lengthen the
		// spawn's pre-IIR frame. A request at or below the rank already held is ignored - feats do not downgrade here
		// (the untrain path is a full reset, ClearCharacterFeats).
		//
		// The replacement keeps the original Sequence: the wire order is TRAIN order, and ranking a feat up is not a
		// new train, so it holds its place in the list.
		// Trains into the character's FIRST spec (index 0). Retail's train packet carries no spec id, and every
		// captured train is on a single-spec character, so which spec a trained feat lands in is not something the
		// corpus answers - spec 0 is the only defensible reading until a multi-spec train is captured (#474).
		public async Task<int> AddCharacterFeats(uint characterId, IEnumerable<uint> featIds)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			// The feat rows are keyed by their spec, so the spec has to exist first. A character that has never had a
			// feat has no spec row either, and creating it here is what makes the first train work.
			if (!await ctx.CharacterFeatSpecs.AnyAsync(s => s.CharacterId == characterId && s.Index == 0))
			{
				ctx.CharacterFeatSpecs.Add(new CharacterFeatSpec
				{
					CharacterId = characterId,
					Index = 0,
					Name = ""
				});
				await ctx.SaveChangesAsync();
			}

			var existing = await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).ToListAsync();
			var byBase = existing.ToDictionary(f => f.FeatId / 100);
			var nextSeq = existing.Count == 0
				? 0
				: existing.Max(f => f.Sequence) + 1;
			var added = 0;
			foreach (var id in featIds)
			{
				var sequence = nextSeq;
				if (byBase.TryGetValue(id / 100, out var held))
				{
					if (held.FeatId % 100 >= id % 100)
						continue; // already at that rank or higher

					sequence = held.Sequence;
					ctx.CharacterFeats.Remove(held); // FeatId is part of the key, so a rank-up is a delete + insert
				}
				else
					nextSeq++;

				var row = new CharacterFeat
				{
					CharacterId = characterId,
					FeatId = id,
					Sequence = sequence
				};
				ctx.CharacterFeats.Add(row);
				byBase[id / 100] = row;
				added++;
			}

			if (added > 0)
				await ctx.SaveChangesAsync();
			return added;
		}

		public async Task<bool> AddCharacterSpell(uint characterId, uint spellId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterSpells.AnyAsync(s => s.CharacterId == characterId && s.SpellId == spellId))
				return false;

			ctx.CharacterSpells.Add(new CharacterSpell
			{
				CharacterId = characterId,
				SpellId = spellId
			});
			await ctx.SaveChangesAsync();
			return true;
		}

		// #915: the spellbook could only ever GROW - `AddCharacterSpell` had no counterpart, so a learned spell was
		// permanent for the life of the character row. Retail has both halves (`GameCharAgent.MagicForgetSpell` c2s,
		// `GameCharInterface.DispatchForgetSpell` s2c), so a shrink is a real operation, not an invention.
		public async Task<bool> RemoveCharacterSpell(uint characterId, uint spellId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterSpells.Where(s => s.CharacterId == characterId && s.SpellId == spellId).ExecuteDeleteAsync() > 0;
		}

		public async Task<int> ClearCharacterSpells(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterSpells.Where(s => s.CharacterId == characterId).ExecuteDeleteAsync();
		}

		public async Task<int> ClearCharacterFeats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).ExecuteDeleteAsync();
		}

		public async Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.CharacterSkills.Where(s => s.CharacterId == characterId).Select(s => s.SkillId).ToListAsync();
			var have = existing.ToHashSet();
			var added = 0;
			foreach (var id in skillIds)
			{
				if (!have.Add(id))
					continue;

				ctx.CharacterSkills.Add(new CharacterSkill
				{
					CharacterId = characterId,
					SkillId = id,
					Rank = 0
				});
				added++;
			}

			if (added > 0)
				await ctx.SaveChangesAsync();
			return added;
		}

		public async Task<CharacterAbility[]> GetCharacterAbilities(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterAbilities.Where(a => a.CharacterId == characterId).OrderBy(a => a.AbilityId).ToArrayAsync();
		}

		public async Task<CharacterCombo[]> GetCharacterCombos(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterCombos.Where(c => c.CharacterId == characterId).OrderBy(c => c.ComboId).ToArrayAsync();
		}

		public async Task<uint[]> GetCharacterSkillIds(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterSkills.AsNoTracking().Where(s => s.CharacterId == characterId).Select(s => s.SkillId).ToArrayAsync();
		}

		public async Task<uint[]> GetCharacterFeats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).OrderBy(f => f.Sequence).Select(f => f.FeatId).ToArrayAsync();
		}

		// The WIRE feat ids of the feats that TEACH any of `spellIds` (#298). The inverse of FeatSpellGrants: a feat
		// template's GameAction kind 53019 `Spell` element names the spell the feat grants, so reading it backwards
		// answers "which feat has to be trained for this spell to be castable".
		//
		// This is the datum the issue was parked on for a month. The workaround it replaces - granting a whole
		// captured character's skill scaffold - existed because the spell->feat mapping was believed to be
		// server-GDB-only; it is in the client RDB, and all 1,510 grant rows resolve to a shipped Spells.Id.
		//
		// Two hops for the same reason GetCharacterFeatEffects has two: FeatSpellGrant.FeatId is the RDB TEMPLATE id
		// and the wire (and CharacterFeat) speak a different id space, which FeatIdMaps bridges. A feat whose
		// template has no wire id contributes nothing - 58 of the 1,510 rows - which is expected, not a fault: it
		// cannot be put on the wire, so training it would be invisible to the client anyway.
		//
		// ⚠️ PROGRESSION TRACKS are excluded, because retail never puts one in a character's feat list and this list
		// goes on the wire. They are the bases whose low digits run past the client's own rank ceiling
		// (<see cref="FeatIdMap.MaxRank" />, FUN_007850c0's `base*100 + 10`) - so the digits are a family INDEX, not
		// a rank: the twelve `<Class> Class Feat 001` tracks with one entry per character level, plus `2HE 60-80`,
		// `Bow 60-80` and `Class Specific HP 60-80`. Filtered per BASE rather than per row, since ranks 0..9 of an
		// 80-entry track are inside the ceiling and would otherwise leak through.
		//
		// The evidence they are not trained is the captured lists themselves: across the four imported characters
		// not one of them appears, and a level-80 Necromancer casts all 109 of her spells carrying 25 feats, none of
		// them her class track. So training one would be inventing wire content to satisfy a criterion that
		// demonstrably does not need it.
		/// <summary>
		///     The WIRE feat ids the CLIENT trains by itself - <see cref="FeatClassEntry.Autotrain" />, straight out
		///     of the client's own featclass table.
		/// </summary>
		/// <remarks>
		///     ⭐ These must never be in a character's feat list (#862). The list goes on the wire; the client builds
		///     its own auto-trained set from this table plus the character's class and level, and
		///     <c>FeatHolder::TrainFeat</c> refuses one outright ("will break feats!"). All 56 feat ids across the
		///     four captured characters are auto-train FALSE, so retail agrees without exception.
		///     <para />
		///     ⚠️ Was <c>Feat.AutomaticUnlockCategory</c> (7063) until #862 - a proxy measured over the
		///     spell-granting feats, which disagrees with the client on 204 of 9,907 rows. The category still
		///     classifies a spell GRANT; this decides what may be trained.
		///     <para />
		///     Used as the free set for <c>FeatRules.PointCost</c>, which should now find nothing to exempt: the
		///     grant that put these in a character's list is gone. It stays as the guard for rows written before it
		///     was removed.
		/// </remarks>
		public async Task<HashSet<uint>> GetAutotrainedFeatWireIds()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return (await ctx.FeatClassEntries.Where(e => e.Autotrain).Select(e => e.FeatId).Distinct().ToListAsync()).ToHashSet();
		}

		/// <summary>
		///     The perk price of each requested feat in one class's tree.
		/// </summary>
		/// <remarks>
		///     ⚠️ A price is attached to a PLACEMENT, not to a feat: <see cref="FeatPerkCost" /> keys on the
		///     <see cref="FeatClassEntry" /> row id, so the same perk in two classes' trees is two rows. Hence the
		///     class parameter - asking without it would price a Conqueror's perk off a Necromancer's tree.
		///     <para />
		///     ⚠️ 216 placements are priced in BOTH specific pools at the same price. The PvE one is taken when both
		///     exist, because that is the pool a PvE server can actually fill; nothing in the client picks for us -
		///     `GetPointCost` renders whichever row it is handed.
		/// </remarks>
		public async Task<Dictionary<uint, (TokenPool Pool, uint Cost)>> GetPerkCosts(ConanClass conanClass, IEnumerable<uint> wireFeatIds)
		{
			var ids = wireFeatIds.Distinct().ToArray();
			if (ids.Length == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.FeatPerkCosts.AsNoTracking()
				.Where(p => ids.Contains(p.Entry.FeatId) && p.Entry.Class == conanClass && p.Entry.TreeId >= AlternateAdvancementFirstTreeId && p.Entry.TreeId <= AlternateAdvancementLastTreeId)
				.Select(p => new { p.Entry.FeatId, p.PointType, p.Cost })
				.ToListAsync();

			return rows
				.GroupBy(r => r.FeatId)
				.ToDictionary(g => g.Key, g =>
				{
					var chosen = g.FirstOrDefault(r => r.PointType == TokenPool.AltAdvancementPvEToken) ?? g.First();

					return (chosen.PointType, chosen.Cost);
				});
		}

		// The twelve ALTERNATE-ADVANCEMENT perk trees, one per playable class: TreeId 1000177..1000190 (the range is
		// not dense - 1000185 and 1000187 do not exist). See FeatClassEntry.TreeId.
		private const uint AlternateAdvancementFirstTreeId = 1000177;
		private const uint AlternateAdvancementLastTreeId = 1000190;

		/// <summary>
		///     The spells a set of just-TRAINED feats teaches - the other half of the spell-grant story (#847).
		/// </summary>
		/// <remarks>
		///     The catalog carries two grant mechanisms and the level path only owns one of them
		///     (<see cref="Feat.AutomaticUnlockCategory" />): the feats a character gets automatically teach the spells
		///     a ding hands out, and the feat TREE teaches the rest - one named feat per spell, bought with a feat
		///     point. This is that second path, so it deliberately EXCLUDES the automatic category: those spells are
		///     the level path's to grant and re-granting them here would re-toast a spell the character already has.
		///     <para>
		///         The oracle is the captured level-80s. Both hold tree spells - the Necromancer <c>Cannibalism</c>,
		///         <c>No Rest For the Dead</c> and <c>Mark of the Devourer</c>, the Demonologist <c>Thunderclap</c>,
		///         <c>Possession</c>, <c>Lore of Skelos</c> and two more - and hold only SOME of the tree, which is
		///         what says they were bought rather than handed out: the same class catalog offers 19 more of exactly
		///         the same kind that neither character has.
		///     </para>
		///     <para>
		///         ⚠️ Takes WIRE feat ids, like <c>CharacterFeat.FeatId</c>, and hops through
		///         <see cref="FeatIdMap" /> to the RDB TEMPLATE id the grant is keyed by - the two id spaces #467
		///         bridges.
		///     </para>
		/// </remarks>
		public async Task<uint[]> GetSpellsTaughtByFeats(IEnumerable<uint> wireFeatIds)
		{
			var wanted = wireFeatIds as IReadOnlyCollection<uint> ?? [.. wireFeatIds];
			if (wanted.Count == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from map in ctx.FeatIdMaps.Where(m => wanted.Contains(m.WireFeatId))
						  join grant in ctx.FeatSpellGrants on map.TemplateId equals grant.FeatId
						  where grant.Feat.TemplateCategory != Feat.AutomaticUnlockCategory
						  select grant.SpellId).Distinct().ToArrayAsync();
		}

		// The prerequisite edges for `featIds` (FeatPrereq, imported from the client's own feat table), as
		// feat -> the ids it depends on. Only the requested feats are loaded: the whole graph is 5501 edges and a
		// train touches a handful.
		public async Task<Dictionary<uint, uint[]>> GetFeatPrereqs(IEnumerable<uint> featIds)
		{
			var wanted = featIds.Distinct().ToArray();
			if (wanted.Length == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.FeatPrereqs.Where(p => wanted.Contains(p.WireFeatId)).OrderBy(p => p.Sequence).ToArrayAsync();
			return rows.GroupBy(p => p.WireFeatId).ToDictionary(g => g.Key, g => g.Select(p => p.RequiresWireFeatId).ToArray());
		}

		// The SINGLE-prerequisite edges of the whole feat graph, as feat -> the one feat it requires (#735). The
		// tradeskill ladders are recovered by walking these, and a walk cannot ask for the ids it has not reached yet -
		// which is why this loads the graph rather than taking a wanted set like the method above.
		//
		// Cheap enough to do so: 5,501 edges, of which only 12 feats carry more than one requirement, and those are
		// skipped rather than picked from - a rung of a ladder has exactly one rung below it, so a multi-prerequisite
		// feat is by construction not one.
		// The TEMPLATE -> WIRE hop, one feat at a time. Null when the template carries no wire id (58 of the client's
		// 1,510 spell-granting feats are in that state), which is a real answer and not a lookup failure: a feat with
		// no wire id can never appear in a character's feat list, so nothing can have trained it.
		public async Task<uint?> GetWireFeatId(uint templateFeatId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var map = await ctx.FeatIdMaps.AsNoTracking().FirstOrDefaultAsync(m => m.TemplateId == templateFeatId);

			return map?.WireFeatId;
		}

		public async Task<Dictionary<uint, uint>> GetSingleFeatPrereqs()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var rows = await ctx.FeatPrereqs.AsNoTracking().ToArrayAsync();
			return rows.GroupBy(p => p.WireFeatId).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().RequiresWireFeatId);
		}

		public async Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterShortcuts.Where(s => s.CharacterId == characterId).OrderBy(s => s.Slot).ToArrayAsync();
		}

		public async Task<CharacterSpell[]> GetCharacterSpells(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterSpells.Where(s => s.CharacterId == characterId).OrderBy(s => s.SpellId).ToArrayAsync();
		}

		public async Task RemoveCharacterShortcut(uint characterId, uint slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterShortcuts.Where(s => s.CharacterId == characterId && s.Slot == slot).ExecuteDeleteAsync();
		}

		public async Task SetCharacterProgress(uint characterId, uint experience, ushort level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Experience, experience).SetProperty(c => c.Level, level));
		}

		public async Task SetAlternateAdvancementPools(uint characterId, uint pvE, uint pvP)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.AaPvEXp, pvE).SetProperty(c => c.AaPvPXp, pvP));
		}

		public async Task<CharacterToken[]> GetCharacterTokens(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			return await ctx.CharacterTokens.AsNoTracking().Where(t => t.CharacterId == characterId).ToArrayAsync();
		}

		public async Task<(ulong Gained, ulong Spent)> AddTokens(uint characterId, TokenPool pool, ulong gained, ulong spent)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var wallet = await ctx.CharacterTokens.FirstOrDefaultAsync(t => t.CharacterId == characterId && t.Pool == pool);
			if (wallet == null)
			{
				wallet = new CharacterToken { CharacterId = characterId, Pool = pool };
				ctx.CharacterTokens.Add(wallet);
			}

			wallet.Gained += gained;
			wallet.Spent += spent;
			await ctx.SaveChangesAsync();

			return (wallet.Gained, wallet.Spent);
		}

		public async Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.CharacterShortcuts.FirstOrDefaultAsync(s => s.CharacterId == characterId && s.Slot == slot);
			if (existing == null)
			{
				ctx.CharacterShortcuts.Add(new CharacterShortcut
				{
					CharacterId = characterId,
					Slot = slot,
					ShortcutType = shortcutType,
					TargetId = targetId
				});
			}
			else
			{
				existing.ShortcutType = shortcutType;
				existing.TargetId = targetId;
			}

			await ctx.SaveChangesAsync();
		}
	}
}

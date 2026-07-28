using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public interface ICharacterRepository
	{
		Task<int> AddCharacterFeats(uint characterId, IEnumerable<uint> featIds);
		Task<bool> AddCharacterSpell(uint characterId, uint spellId);
		Task<bool> AddFriend(uint characterId, uint friendCharacterId);
		Task<Character[]> GetFriends(uint characterId);
		Task<uint[]> GetFriendWatchers(uint characterId);
		Task<bool> RemoveFriend(uint characterId, uint friendCharacterId);
		Task AddCharacterTag(uint characterId, uint tagId);
		Task<int> ClearCharacterFeats(uint characterId);
		Task<Character> CreateCharacter(uint accountId, uint realmId);
		Task<bool> DeleteCharacter(uint characterId);
		Task DiscoverRespawnPoint(uint characterId, uint respawnPointId);
		Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds);
		Task<Character?> GetCharacter(uint id);
		Task<CharacterAbility[]> GetCharacterAbilities(uint characterId);
		Task<CharacterBuff[]> GetCharacterBuffs(uint characterId);
		Task<Character?> GetCharacterByName(string name, uint realmId);
		Task<CharacterCombo[]> GetCharacterCombos(uint characterId);
		Task<CharacterEquipment?> GetCharacterEquipment(uint characterId);
		Task<uint[]> GetCharacterFeats(uint characterId);
		Task<Dictionary<uint, uint[]>> GetFeatPrereqs(IEnumerable<uint> featIds);
		Task<FeatEffect[]> GetCharacterFeatEffects(uint characterId);
		Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<CharacterInventory[]> GetCharacterInventory(uint characterId);
		Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint questId);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task<CharacterStat[]> GetCharacterStats(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);
		Task<Character[]> GetCharactersByAccount(uint id);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);
		Task<Item[]> GetEquippedStatItems(uint characterId, WeaponSet activeSet);

		// The class/level BASE stats (#444): what a character of this class and level starts with before gear and buffs.
		// Empty when that (class, level) has no decoded row - all 12 classes x 80 levels are shipped, so this means a
		// level outside 1..80 or an unimported database - and the caller must treat empty as UNKNOWN, not as zero.
		Task<IReadOnlyDictionary<CharacterStatId, int>> GetClassAttributeBase(ConanClass characterClass, uint level);
		Task<(int Min, int Max)?> GetEquippedWeaponDamage(uint characterId);
		Task RemoveCharacterQuest(uint characterId, uint questId);
		Task RemoveCharacterShortcut(uint characterId, uint slot);
		Task SaveCharacter(Character character);
		Task SaveCharacterBind(uint characterId, uint respawnPointId);
		Task SaveCharacterBuffs(uint characterId, IReadOnlyCollection<CharacterBuff> buffs);
		Task SaveCharacterDeadState(uint characterId, bool isDead);
		Task SaveCharacterEquipment(CharacterEquipment equipment);
		Task SaveCharacterMount(uint characterId, uint? mountId);
		Task SaveCharacterScaffold(Character character);
		Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId);
		Task SaveCharacterWeaponSet(uint characterId, WeaponSet activeSet);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank);
		Task SetCharacterInventorySlot(uint characterId, ContainerKind container, int slot, uint? itemId, uint count = 1);
		Task SetCharacterMoney(uint characterId, ulong money);
		Task SetCharacterKillCounts(uint characterId, uint kills, uint deaths);

		Task SetCharacterProgress(uint characterId, uint experience, ushort level);
		Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string? goalTargetName = null);
		Task SetCharacterQuestGoal(uint characterId, uint questId, uint goalId, uint progress);
		Task SetCharacterShortcut(uint characterId, uint slot, uint shortcutType, uint targetId);
		Task<bool> SetCreationSpawn(uint characterId);
		Task<bool> SetVanityFilter(uint characterId, uint slot, bool toggleState);
		Task<bool> UpdateLastInfo(uint characterId, string ipAddress);
	}

	public partial class DatabaseRepository
	{
		// Character ids ARE the on-wire client instance (see EntityHandles for the whole number line and why the
		// ceiling exists). Allocated from the base upward, continuing the existing 0x013xxxxx rows.
		private const uint FirstCharacterId = EntityHandles.CharacterInstanceBase;
		private const uint MaxCharacterId = EntityHandles.MaxCharacterId;

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

		// One-sided by design: this writes the caller's row only, never a reciprocal one (see CharacterFriend). Returns
		// false when the pair is already there, so a re-add is a no-op rather than a duplicate-key throw.
		public async Task<bool> AddFriend(uint characterId, uint friendCharacterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterFriends.AnyAsync(f => f.CharacterId == characterId && f.FriendCharacterId == friendCharacterId))
				return false;

			ctx.CharacterFriends.Add(new CharacterFriend
			{
				CharacterId = characterId,
				FriendCharacterId = friendCharacterId
			});
			await ctx.SaveChangesAsync();
			return true;
		}

		// The characters who have `characterId` on THEIR list - the reverse direction, which the one-sided list makes a
		// genuinely different query. Needed to tell them when this character comes online or goes offline; without it a
		// friend's status is frozen at whatever it was when the watcher logged in.
		public async Task<uint[]> GetFriendWatchers(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFriends.Where(f => f.FriendCharacterId == characterId).Select(f => f.CharacterId).ToArrayAsync();
		}

		// The befriended CHARACTERS, not the join rows - every caller needs the name and class to build the wire record.
		public async Task<Character[]> GetFriends(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFriends.Where(f => f.CharacterId == characterId).Include(f => f.Friend).Select(f => f.Friend).AsNoTracking().ToArrayAsync();
		}

		public async Task<bool> RemoveFriend(uint characterId, uint friendCharacterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFriends.Where(f => f.CharacterId == characterId && f.FriendCharacterId == friendCharacterId).ExecuteDeleteAsync() > 0;
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

		public async Task AddCharacterTag(uint characterId, uint tagId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterTags.AnyAsync(t => t.CharacterId == characterId && t.TagId == tagId))
				return;

			ctx.CharacterTags.Add(new CharacterTag
			{
				CharacterId = characterId,
				TagId = tagId
			});
			await ctx.SaveChangesAsync();
		}

		public async Task<int> ClearCharacterFeats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).ExecuteDeleteAsync();
		}

		public async Task<Character> CreateCharacter(uint accountId, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var character = await ctx.Characters.FirstOrDefaultAsync(c => c.AccountId == accountId && c.Name == null && c.Id <= MaxCharacterId);
			if (character == null)
			{
				var stale = await ctx.Characters.Where(c => c.AccountId == accountId && c.Name == null).ToListAsync();
				if (stale.Count > 0)
				{
					ctx.Characters.RemoveRange(stale);
					await ctx.SaveChangesAsync();
				}

				var maxInRange = await ctx.Characters.Where(c => c.Id <= MaxCharacterId).Select(c => (uint?)c.Id).MaxAsync() ?? FirstCharacterId - 1;
				var nextId = maxInRange + 1;
				if (nextId > MaxCharacterId)
					throw new InvalidOperationException($"Character id space exhausted in the 4-byte varint range (max {MaxCharacterId:X}).");

				character = new Character
				{
					Id = nextId,
					AccountId = accountId,
					RealmId = realmId,
					AuthChallenge = Guid.NewGuid(),
					Level = 1,
					Class = ConanClass.Barbarian,
					Race = Race.Cimmerian,
					CreationDate = DateTimeOffset.UtcNow,
					MapId = Map.NewPlayerSpawnSpawnPoint.MapId,
					Position = Map.NewPlayerSpawnSpawnPoint.Position
				};

				character.MaxStats = CharacterMaxStats.CreateDefaults(character.Class, character.Level);

				// Every character gets its equipment row HERE, with the character itself, so "a character has one" is an
				// invariant established at creation rather than something each reader has to cope with. The slots are all
				// empty - the first-login starter gear (#220) fills them - but the ROW exists, which is what the spawn and
				// equip paths depend on: they now throw on a missing one instead of substituting an empty object, and that
				// throw is only ever corruption if this holds.
				character.Equipment = new CharacterEquipment
				{
					CharacterId = character.Id
				};

				ctx.Characters.Add(character);
				await ctx.SaveChangesAsync();
			}

			return character;
		}

		public async Task<bool> DeleteCharacter(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.Where(a => a.Id == characterId).ExecuteDeleteAsync() == 1;
		}

		public async Task DiscoverRespawnPoint(uint characterId, uint respawnPointId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var exists = await ctx.CharacterRespawnPoints.AnyAsync(c => c.CharacterId == characterId && c.RespawnPointId == respawnPointId);
			if (exists)
				return;

			ctx.CharacterRespawnPoints.Add(new CharacterRespawnPoint
			{
				CharacterId = characterId,
				RespawnPointId = respawnPointId
			});
			await ctx.SaveChangesAsync();
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

		public async Task<Character?> GetCharacter(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.WithSessionNavigations().FirstOrDefaultAsync(c => c.Id == id);
		}

		public async Task<CharacterAbility[]> GetCharacterAbilities(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterAbilities.Where(a => a.CharacterId == characterId).OrderBy(a => a.AbilityId).ToArrayAsync();
		}

		public async Task<CharacterBuff[]> GetCharacterBuffs(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterBuffs.Where(b => b.CharacterId == characterId).OrderBy(b => b.BuffId).ToArrayAsync();
		}

		public async Task<Character?> GetCharacterByName(string name, uint realmId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.FirstOrDefaultAsync(c => c.RealmId == realmId && c.Name == name);
		}

		public async Task<CharacterCombo[]> GetCharacterCombos(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterCombos.Where(c => c.CharacterId == characterId).OrderBy(c => c.ComboId).ToArrayAsync();
		}

		public async Task<CharacterEquipment?> GetCharacterEquipment(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.CharacterEquipment.Include(e => e.Neck)
				.Include(e => e.Head)
				.Include(e => e.Bag)
				.Include(e => e.ResourceBag)
				.Include(e => e.Chest)
				.Include(e => e.MainHandLeft)
				.Include(e => e.MainHandRight)
				.Include(e => e.OffHandLeft)
				.Include(e => e.OffHandRight)
				.Include(e => e.Belt)
				.Include(e => e.RingLeft)
				.Include(e => e.RingRight)
				.Include(e => e.Cloak)
				.Include(e => e.Shoulder)
				.Include(e => e.Boots)
				.Include(e => e.Hands)
				.Include(e => e.Legs)
				.Include(e => e.FirstPosBag)
				.Include(e => e.VanityHead)
				.Include(e => e.VanityChest)
				.Include(e => e.VanityBelt)
				.Include(e => e.VanityCape)
				.Include(e => e.VanityBoots)
				.Include(e => e.VanityWrist)
				.Include(e => e.Wrist)
				.FirstOrDefaultAsync(e => e.CharacterId == characterId);
		}

		public async Task<uint[]> GetCharacterFeats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).OrderBy(f => f.Sequence).Select(f => f.FeatId).ToArrayAsync();
		}

		// The stat modifiers a character's trained feats grant. Two hops, because the wire and the catalog use
		// different id spaces (#467): CharacterFeat.FeatId is a WIRE id, FeatIdMap resolves it to the RDB TEMPLATE id,
		// and FeatEffect hangs off the template.
		//
		// A feat whose template is one of the TEA-encrypted ones contributes nothing - we cannot read its effects -
		// so this returns fewer rows than the character has feats, which is expected rather than a fault.
		public async Task<FeatEffect[]> GetCharacterFeatEffects(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
				join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
				join effect in ctx.FeatEffects on map.TemplateId equals effect.FeatId
				select effect).ToArrayAsync();
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

		public async Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.WithSessionNavigations().FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
		}

		public async Task<CharacterInventory[]> GetCharacterInventory(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterInventory.Include(i => i.Item).Where(i => i.CharacterId == characterId).ToArrayAsync();
		}

		public async Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuestGoals.Where(g => g.CharacterId == characterId && g.QuestId == questId).ToArrayAsync();
		}

		public async Task<CharacterQuest[]> GetCharacterQuests(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuests.Where(q => q.CharacterId == characterId).ToArrayAsync();
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

		public async Task<CharacterStat[]> GetCharacterStats(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterStats.Where(s => s.CharacterId == characterId).OrderBy(s => s.Ordinal).ToArrayAsync();
		}

		public async Task<uint[]> GetCharacterTags(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterTags.Where(t => t.CharacterId == characterId).Select(t => t.TagId).ToArrayAsync();
		}

		// The account's PLAYABLE characters - a nameless row is a create in progress, never something to list. That was
		// a `skipUninitialized = true` parameter no call site ever passed false to, i.e. a switch for a behaviour
		// nobody wanted.
		public async Task<Character[]> GetCharactersByAccount(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.Where(c => c.AccountId == id && c.Name != null).ToArrayAsync();
		}

		public async Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterRespawnPoints.Where(c => c.CharacterId == characterId).Select(c => c.RespawnPointId).ToArrayAsync();
		}

		// The character's worn STAT-BEARING items (the 16 combat slots) with their StatModifiers loaded, one entry per
		// occupied slot (two identical rings -> the item twice), for server-side gear-stat aggregation (#3). Reads the
		// equipment's *Id columns (no nav load), then fetches only those Items + their modifiers. Empty when the
		// character has no equipment row or no stat-bearing gear.
		// The class/level BASE stats (#444). Empty when that (class, level) has no decoded row - some classes' higher
		// levels are not located yet - and the caller MUST treat empty as unknown rather than as zero.
		public async Task<IReadOnlyDictionary<CharacterStatId, int>> GetClassAttributeBase(ConanClass characterClass, uint level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ClassAttributeBases.AsNoTracking()
				.Where(r => r.Class == characterClass && r.Level == level)
				.ToDictionaryAsync(r => r.StatId, r => r.Amount);
		}

		public async Task<Item[]> GetEquippedStatItems(uint characterId, WeaponSet activeSet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var equipment = await ctx.CharacterEquipment.AsNoTracking().FirstOrDefaultAsync(e => e.CharacterId == characterId);
			if (equipment == null)
				return [];

			// #400: fold in only the ACTIVE weapon set's two weapon slots (armour + jewellery always apply); counting
			// both the melee and ranged sets when both are equipped would double-count.
			var ids = equipment.StatBearingItemIds(activeSet).ToHashSet();
			if (ids.Count == 0)
				return [];

			var byId = await ctx.Items.AsNoTracking().Include(i => i.StatModifiers).Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
			// Preserve one entry per occupied slot (duplicates included) so a stat granted by two worn copies stacks.
			return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
		}

		// The (min, max) damage of the character's equipped MELEE main-hand weapon (MainHandLeft), for melee resolution
		// (#395), or null if that slot is empty or the item carries no imported weapon damage. A lightweight projection -
		// no nav graph loaded (the combat swing hot path).
		public async Task<(int Min, int Max)?> GetEquippedWeaponDamage(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var dmg = await ctx
				.CharacterEquipment.Where(e => e.CharacterId == characterId && e.MainHandLeftId != null)
				.Join(ctx.Items, e => e.MainHandLeftId, i => i.Id, (_, i) => new
				{
					i.MinDamage,
					i.MaxDamage
				})
				.FirstOrDefaultAsync();

			return dmg is { MinDamage: { } min, MaxDamage: { } max }
				? (min, max)
				: null;
		}

		public async Task RemoveCharacterQuest(uint characterId, uint questId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && q.QuestId == questId).ExecuteDeleteAsync();
		}

		public async Task RemoveCharacterShortcut(uint characterId, uint slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterShortcuts.Where(s => s.CharacterId == characterId && s.Slot == slot).ExecuteDeleteAsync();
		}

		public async Task SaveCharacter(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			ctx.Characters.Update(character);

			foreach (var entry in ctx.ChangeTracker.Entries().Where(e => e is { State: EntityState.Modified, Entity: not Character }).ToList())
			{
				if (await entry.GetDatabaseValuesAsync() is null)
					entry.State = EntityState.Added;
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterBind(uint characterId, uint respawnPointId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.BindRespawnPointId, respawnPointId));
		}

		public async Task SaveCharacterBuffs(uint characterId, IReadOnlyCollection<CharacterBuff> buffs)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			ctx.CharacterBuffs.RemoveRange(ctx.CharacterBuffs.Where(b => b.CharacterId == characterId));
			foreach (var buff in buffs)
			{
				buff.CharacterId = characterId;
				buff.Character = null!;
				ctx.CharacterBuffs.Add(buff);
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterDeadState(uint characterId, bool isDead)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDead, isDead));
		}

		public async Task SaveCharacterEquipment(CharacterEquipment equipment)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var tracked = await LoadTrackedEquipment(ctx, equipment.CharacterId) ?? AddNewEquipment(ctx, equipment.CharacterId);

			tracked.Neck = await ResolveItem(ctx, equipment.Neck?.Id);
			tracked.Head = await ResolveItem(ctx, equipment.Head?.Id);
			tracked.Bag = await ResolveItem(ctx, equipment.Bag?.Id);
			tracked.ResourceBag = await ResolveItem(ctx, equipment.ResourceBag?.Id);
			tracked.Chest = await ResolveItem(ctx, equipment.Chest?.Id);
			tracked.MainHandLeft = await ResolveItem(ctx, equipment.MainHandLeft?.Id);
			tracked.MainHandRight = await ResolveItem(ctx, equipment.MainHandRight?.Id);
			tracked.OffHandLeft = await ResolveItem(ctx, equipment.OffHandLeft?.Id);
			tracked.OffHandRight = await ResolveItem(ctx, equipment.OffHandRight?.Id);
			tracked.Belt = await ResolveItem(ctx, equipment.Belt?.Id);
			tracked.RingLeft = await ResolveItem(ctx, equipment.RingLeft?.Id);
			tracked.RingRight = await ResolveItem(ctx, equipment.RingRight?.Id);
			tracked.Cloak = await ResolveItem(ctx, equipment.Cloak?.Id);
			tracked.Shoulder = await ResolveItem(ctx, equipment.Shoulder?.Id);
			tracked.Boots = await ResolveItem(ctx, equipment.Boots?.Id);
			tracked.Hands = await ResolveItem(ctx, equipment.Hands?.Id);
			tracked.Legs = await ResolveItem(ctx, equipment.Legs?.Id);
			tracked.FirstPosBag = await ResolveItem(ctx, equipment.FirstPosBag?.Id);
			tracked.VanityHead = await ResolveItem(ctx, equipment.VanityHead?.Id);
			tracked.VanityChest = await ResolveItem(ctx, equipment.VanityChest?.Id);
			tracked.VanityBelt = await ResolveItem(ctx, equipment.VanityBelt?.Id);
			tracked.VanityCape = await ResolveItem(ctx, equipment.VanityCape?.Id);
			tracked.VanityBoots = await ResolveItem(ctx, equipment.VanityBoots?.Id);
			tracked.VanityWrist = await ResolveItem(ctx, equipment.VanityWrist?.Id);
			tracked.Wrist = await ResolveItem(ctx, equipment.Wrist?.Id);

			await ctx.SaveChangesAsync();

			return;

			static async Task<Item?> ResolveItem(DatabaseContext ctx, uint? id)
			{
				return id == null
					? null
					: await ctx.Items.FindAsync(id.Value);
			}

			static CharacterEquipment AddNewEquipment(DatabaseContext ctx, uint characterId)
			{
				var equipment = new CharacterEquipment
				{
					CharacterId = characterId
				};
				ctx.CharacterEquipment.Add(equipment);
				return equipment;
			}

			static Task<CharacterEquipment?> LoadTrackedEquipment(DatabaseContext ctx, uint characterId)
			{
				return ctx
					.CharacterEquipment.Include(e => e.Neck)
					.Include(e => e.Head)
					.Include(e => e.Bag)
					.Include(e => e.ResourceBag)
					.Include(e => e.Chest)
					.Include(e => e.MainHandLeft)
					.Include(e => e.MainHandRight)
					.Include(e => e.OffHandLeft)
					.Include(e => e.OffHandRight)
					.Include(e => e.Belt)
					.Include(e => e.RingLeft)
					.Include(e => e.RingRight)
					.Include(e => e.Cloak)
					.Include(e => e.Shoulder)
					.Include(e => e.Boots)
					.Include(e => e.Hands)
					.Include(e => e.Legs)
					.Include(e => e.FirstPosBag)
					.Include(e => e.VanityHead)
					.Include(e => e.VanityChest)
					.Include(e => e.VanityBelt)
					.Include(e => e.VanityCape)
					.Include(e => e.VanityBoots)
					.Include(e => e.VanityWrist)
					.Include(e => e.Wrist)
					.FirstOrDefaultAsync(e => e.CharacterId == characterId);
			}
		}

		public async Task SaveCharacterMount(uint characterId, uint? mountId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.MountId, mountId));
		}

		public async Task SaveCharacterScaffold(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			ctx.CharacterAttributes.RemoveRange(ctx.CharacterAttributes.Where(a => a.CharacterId == character.Id));
			ctx.CharacterSkills.RemoveRange(ctx.CharacterSkills.Where(s => s.CharacterId == character.Id));
			ctx.CharacterScaffoldPairs.RemoveRange(ctx.CharacterScaffoldPairs.Where(p => p.CharacterId == character.Id));

			ctx.CharacterAttributes.AddRange(character.Attributes);
			ctx.CharacterSkills.AddRange(character.Skills);
			ctx.CharacterScaffoldPairs.AddRange(character.ScaffoldPairs);

			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx
				.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.PositionX, position.X)
					.SetProperty(c => c.PositionY, position.Y)
					.SetProperty(c => c.PositionZ, position.Z)
					.SetProperty(c => c.RotationX, rotation.X)
					.SetProperty(c => c.RotationY, rotation.Y)
					.SetProperty(c => c.RotationZ, rotation.Z)
					.SetProperty(c => c.RotationW, rotation.W)
					.SetProperty(c => c.MapId, mapId));
		}

		// #400: persist the character's drawn weapon SET after a SwitchWeaponsButton toggle, so the active loadout (and
		// the login-time gear-stat aggregation that counts only its weapons) survives relog. Targeted update, no load.
		public async Task SaveCharacterWeaponSet(uint characterId, WeaponSet activeSet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ActiveWeaponSet, activeSet));
		}

		public async Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.GuildId, guildId).SetProperty(c => c.GuildRank, rank)) == 1;
		}

		public async Task SetCharacterInventorySlot(uint characterId, ContainerKind container, int slot, uint? itemId, uint count = 1)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterInventory.FirstOrDefaultAsync(i => i.CharacterId == characterId && i.Container == container && i.Slot == slot);

			if (itemId == null)
			{
				if (row == null)
					return;

				ctx.CharacterInventory.Remove(row);
			}
			else if (row == null)
			{
				ctx.CharacterInventory.Add(new CharacterInventory
				{
					CharacterId = characterId,
					Container = container,
					Slot = slot,
					ItemId = itemId,
					Count = count
				});
			}
			else
			{
				row.ItemId = itemId;
				row.Count = count;
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SetCharacterMoney(uint characterId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, money));
		}

		// The kill/death counters, persisted immediately like SetCharacterProgress: they are earned one event at a
		// time, and a crash between the event and the autosave would silently lose the count.
		public async Task SetCharacterKillCounts(uint characterId, uint kills, uint deaths)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Kills, kills).SetProperty(c => c.Deaths, deaths));
		}

		public async Task SetCharacterProgress(uint characterId, uint experience, ushort level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Experience, experience).SetProperty(c => c.Level, level));
		}

		public async Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string? goalTargetName = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuests.FirstOrDefaultAsync(q => q.CharacterId == characterId && q.QuestId == questId);
			if (row == null)
			{
				ctx.CharacterQuests.Add(new CharacterQuest
				{
					CharacterId = characterId,
					QuestId = questId,
					State = state,
					GoalProgress = goalProgress,
					GoalRequired = goalRequired,
					GoalTargetName = goalTargetName
				});
			}
			else
			{
				row.State = state;
				row.GoalProgress = goalProgress;
				row.GoalRequired = goalRequired;
				row.GoalTargetName = goalTargetName;
			}

			await ctx.SaveChangesAsync();
		}

		public async Task SetCharacterQuestGoal(uint characterId, uint questId, uint goalId, uint progress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterQuestGoals.FirstOrDefaultAsync(g => g.CharacterId == characterId && g.QuestId == questId && g.GoalId == goalId);
			if (row == null)
			{
				ctx.CharacterQuestGoals.Add(new CharacterQuestGoal
				{
					CharacterId = characterId,
					QuestId = questId,
					GoalId = goalId,
					Progress = progress
				});
			}
			else
				row.Progress = progress;

			await ctx.SaveChangesAsync();
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

		public async Task<bool> SetCreationSpawn(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.MapId, Map.NewPlayerSpawnSpawnPoint.MapId)
					.SetProperty(c => c.PositionX, Map.NewPlayerSpawnSpawnPoint.Position.X)
					.SetProperty(c => c.PositionY, Map.NewPlayerSpawnSpawnPoint.Position.Y)
					.SetProperty(c => c.PositionZ, Map.NewPlayerSpawnSpawnPoint.Position.Z)) == 1;
		}

		public async Task<bool> SetVanityFilter(uint characterId, uint slot, bool toggleState)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var equipment = await ctx.CharacterEquipment.FirstOrDefaultAsync(e => e.CharacterId == characterId);
			if (equipment == null || !equipment.SetVanityFilter(slot, toggleState))
				return false;

			await ctx.SaveChangesAsync();
			return true;
		}

		public async Task<bool> UpdateLastInfo(uint characterId, string ipAddress)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters
				.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(a => a.LastConnection, DateTimeOffset.UtcNow)
					.SetProperty(a => a.LastIpAddress, ipAddress)) == 1;
		}
	}

	internal static class CharacterQueryExtensions
	{
		// The navigations a Character needs to describe itself completely: the transform/appearance rows plus the
		// spawn-scaffold set the FA015807 record is generated from. Lazy loading is deliberately off, so a navigation
		// that is not included here reads as EMPTY rather than failing - which makes the include list part of the
		// contract, not a performance knob. One list, used by every loader that produces a long-lived Character, so
		// the session copy and the scaffold copy cannot describe the same character differently.
		public static IQueryable<Character> WithSessionNavigations(this IQueryable<Character> characters)
		{
			return characters
				.Include(c => c.Account)
				.Include(c => c.Body)
				.Include(c => c.BodyResources)
				.Include(c => c.MaxStats)
				.Include(c => c.Equipment)
				.Include(c => c.Attributes)
				.Include(c => c.Skills)
				.Include(c => c.Feats)
				.Include(c => c.FeatSpecs)
				.Include(c => c.ScaffoldPairs);
		}
	}
}

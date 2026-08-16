using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Progression;
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
		Task<int> ClearCharacterFeats(uint characterId);
		Task<Character> CreateCharacter(uint accountId, uint realmId);
		Task<bool> DeleteCharacter(uint characterId);
		Task DiscoverRespawnPoint(uint characterId, uint respawnPointId);
		Task<int> EnsureCharacterSkills(uint characterId, IEnumerable<uint> skillIds);
		Task<Character?> GetCharacter(uint id);
		Task<CharacterAbility[]> GetCharacterAbilities(uint characterId);
		Task<CharacterBuff[]> GetCharacterBuffs(uint characterId);
		Task<CharacterClientSettings?> GetCharacterClientSettings(uint characterId);
		Task<CharacterClientSettings[]> GetCharacterClientSettingsByAccount(uint accountId);
		Task SaveCharacterClientSettings(uint characterId, byte[]? settings, byte[]? preview);
		Task<Character?> GetCharacterByName(string name, uint realmId);
		Task<CharacterCombo[]> GetCharacterCombos(uint characterId);
		/// <summary>
		///     The character's WORN set (container <see cref="ContainerKind.Equipped" />) as slot -> the item instance in
		///     it, templates loaded. Vanity slots (1000 + the base slot) are included: they are real equipped-container
		///     positions, not a display-only overlay, and a captured L80 snapshot carries pos 1002/1007/1011/1014/1018.
		/// </summary>
		Task<IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>> GetEquippedItems(uint characterId);
		Task<uint[]> GetCharacterFeats(uint characterId);
		Task<FeatSpellStatModifier[]> GetCharacterFeatSpellStatModifiers(uint characterId);

		Task<uint[]> GetFeatsTeachingSpells(IEnumerable<uint> spellIds);

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
		Task<FeatEffect[]> GetCharacterFeatEffects(uint characterId);

		/// <summary>
		///     The per-SPELL boosts a character's trained feats grant (#590), as <c>spellId -> percent x 100</c>.
		///     Resolved through the spell LINE the feat actually names, so every rank of a boosted spell is in the
		///     result and combat needs no line lookup of its own. Summed per spell, because two different feats can
		///     boost the same line; a character holds only the HIGHEST rank of each feat, so the ranks of one feat
		///     never stack with each other.
		/// </summary>
		Task<IReadOnlyDictionary<uint, int>> GetCharacterSpellBoosts(uint characterId);

		/// <summary>
		///     What a character's trained feats do to their COMBOS (#697), as <c>comboId -> bonus</c>. Summed per
		///     combo across every feat that names it, on the same "a character holds only the highest rank of each
		///     feat" rule <see cref="GetCharacterSpellBoosts" /> relies on.
		/// </summary>
		Task<IReadOnlyDictionary<uint, ComboFeatBonus>> GetCharacterComboBonuses(uint characterId);

		/// <summary>
		///     The per-spell scaling-attribute OVERRIDES among a character's KNOWN spells (#592), as
		///     <c>spellId -> wire stat id</c>. Only spells whose template carries RDB field 847 appear; every other
		///     spell takes the caster's class attribute (<c>CombatResolver.MagicDamageAttribute</c>), so an absent
		///     entry means "no override", not "no scaling".
		/// </summary>
		Task<IReadOnlyDictionary<uint, uint>> GetCharacterSpellScalingAttributes(uint characterId);
		Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance);
		/// <summary>
		///     The character's item instances, template + sockets loaded. <paramref name="container" /> filters in SQL to
		///     one container (the bag, the bank); null (the default) returns every item the character holds, worn
		///     included, which is what the world-entry snapshot wants.
		/// </summary>
		Task<ItemInstance[]> GetCharacterItems(uint characterId, ContainerKind? container = null);

		/// <summary>
		///     The item in ONE cell, or null when it is empty. The equip / item-use / trade / tradepost paths all want a
		///     single (container, slot) and would otherwise read the whole inventory to pick it out in memory.
		/// </summary>
		Task<ItemInstance?> GetCharacterItemSlot(uint characterId, ContainerKind container, int slot);
		/// <summary>
		///     A character's secondary-goal progress rows. <paramref name="questId" /> filters to one quest; null (the
		///     default) returns every held quest's rows in ONE query, which is what the world-entry journal wants - it
		///     needs the progress of every active quest's goals and would otherwise issue one call per quest.
		/// </summary>
		Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint? questId = null);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId, QuestState? state = null);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task<CharacterStat[]> GetCharacterStats(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);

		// #748: grant a tag (an achievement, or an item-shop "bundle shown" flag - one id space). False = already held.
		Task<bool> AddCharacterTag(uint characterId, uint tagId);

		// The achievement a tag id names, or null for an id the client's catalog does not carry (LDB 55701).
		Task<Achievement?> GetAchievement(uint tagId);

		// Achievements whose NAME contains `nameSubstring`, capped at `limit` - a 4,095-entry catalog needs a lookup.
		Task<Achievement[]> FindAchievements(string nameSubstring, int limit);
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

		/// <summary>
		///     Sets the given stat sheet slots to new values (#739 - the skillpoint spend). A stat id may occupy more
		///     than one ordinal (StatList#1 mirrors displayed stats that StatList#2 also carries), so EVERY row
		///     holding the id is written - the sheet must not disagree with itself. Ids with no row are ignored: the
		///     sheet is what the snapshot is authored from, and inventing an ordinal would put a stat on the wire in
		///     a position no capture has.
		/// </summary>
		Task SaveCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values);
		Task SaveCharacterMount(uint characterId, uint? mountId);
		Task SaveCharacterScaffold(Character character);
		Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId);
		Task SaveCharacterWeaponSet(uint characterId, WeaponSet activeSet);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank);
		/// <summary>
		///     MINTS a new item in a cell, or CLEARS the cell when <paramref name="templateId" /> is null. Whatever was
		///     there is deleted, sockets and all - so this is the right call for a grant, a purchase, a loot pickup or a
		///     split's new half, and the WRONG one for a move: use <see cref="MoveCharacterItem" />, which preserves the
		///     instance. Returns the new instance (null when the cell was cleared).
		/// </summary>
		Task<ItemInstance?> SetCharacterItemSlot(uint characterId, ContainerKind container, int slot, uint? templateId, uint count = 1, string? creatorName = null);

		/// <summary>
		///     MOVES the item at one cell to another, swapping with the destination's occupant when there is one. The
		///     instances are UPDATED in place, never re-minted, so the crafter, the stack count and the socketed gems
		///     travel with the item by construction - which is the whole point of an item being a row rather than an
		///     address (#750). Returns false when the source cell is empty.
		/// </summary>
		Task<bool> MoveCharacterItem(uint characterId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot);

		/// <summary>
		///     Marks one item instance BOUND to whoever holds it (#756) - the per-instance half of binding, set when a
		///     bind-on-equip item is first worn. Returns false when the instance is gone or was already bound, so the
		///     caller only announces a change that happened.
		///     <para>
		///         The RULE ("does this template bind on equip") is NOT decided here: that is <c>Item.BindsOnEquip</c>,
		///         and the caller applies it. This only persists the outcome.
		///     </para>
		/// </summary>
		Task<bool> BindCharacterItem(uint itemInstanceId);

		/// <summary>
		///     Puts a GEM into one socket of an item and CONSUMES the gem's own instance, in one transaction (#754).
		///     Returns the new socket with its gem template loaded, or null when either instance is gone or that
		///     socket index is already taken.
		///     <para>
		///         The RULES - is the item socketable, is there a free index, does the colour match - are NOT decided
		///         here; <c>TradeskillService.Socket</c> applies them. This is the write, and it is one transaction
		///         because the two halves must not come apart: a socket without the gem removed duplicates it, and a
		///         gem removed without the socket destroys it.
		///     </para>
		/// </summary>
		Task<ItemInstanceSocket?> SocketGem(uint itemInstanceId, uint socketIndex, uint gemInstanceId);

		/// <summary>The GUILD BANK vault's contents (#654), template + sockets loaded, ordered by cell.</summary>
		Task<IReadOnlyList<ItemInstance>> GetGuildBankItems(uint guildId);

		/// <summary>
		///     Moves one item between a member's own container and their guild's vault (#654), in either direction -
		///     the wire has ONE symmetric message for both. Returns false when the source cell is empty, the
		///     destination is occupied, or neither end is the vault.
		///     <para>
		///         The instance is UPDATED, never re-minted, so the crafter, the stack and the gems travel with it -
		///         and OWNERSHIP moves with it: into the vault clears <c>OwnerCharacterId</c> and sets
		///         <c>OwnerGuildId</c>, out of it does the reverse. Exactly one is ever set.
		///     </para>
		/// </summary>
		Task<bool> MoveGuildBankItem(uint characterId, uint guildId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot);

		/// <summary>Sets an existing stack's size. Returns false when the cell is empty.</summary>
		Task<bool> SetCharacterItemCount(uint characterId, ContainerKind container, int slot, uint count);

		/// <summary>
		///     HANDS an item to another character: the same instance, re-owned into their cell, so its crafter, stack and
		///     socketed gems travel with it (#750). Returns the instance, or null when it no longer exists or the target
		///     cell is occupied - in which case NOTHING has moved and the item is still where it was.
		///     <para>
		///         ⚠️ This is what a player TRADE is, and it must not be a delete-plus-mint. Re-minting deleted the
		///         giver's row (cascading its sockets) and handed the receiver a gemless copy with no crafter.
		///     </para>
		/// </summary>
		Task<ItemInstance?> TransferCharacterItem(uint instanceId, uint toCharacterId, ContainerKind container, int slot);
		Task SetCharacterMoney(uint characterId, ulong money);
		Task SetCharacterNpcKillCounts(uint characterId, uint kills, uint deaths);

		// The PvP criminal state (#503): when the criminal debuff expires (null = not a criminal) and the murder-point
		// pool. Persisted immediately, like the kill counters - the debuff has to survive a relog and a zone change,
		// which is what the annotated capture shows it doing.
		Task SetCharacterCriminalState(uint characterId, DateTimeOffset? criminalUntil, uint murderPoints);

		/// <summary>Stat 0x0A9 `HeroicMode` (#625) - the raw wire value, see Character.HeroicMode.</summary>
		Task SetCharacterHeroicMode(uint characterId, uint heroicMode);

		Task SetCharacterProgress(uint characterId, uint experience, ushort level);
		Task SetCharacterQuest(uint characterId, uint questId, QuestState state, uint goalProgress = 0, uint goalRequired = 0, string? goalTargetName = null);

		// Record the pick-one-of-N reward `characterId` took for `questId` (#815) - the idempotence guard for the grant.
		Task SetCharacterQuestReward(uint characterId, uint questId, uint chosenPos);

		/// <summary>Records the Tortage tutorial hints a character has been shown, as a bitmask (#461).</summary>
		Task SetCharacterTutorialSteps(uint characterId, ulong stepsSeen);
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

		// #748: returns FALSE when the character already held the tag. It used to return nothing, which was fine while
		// it had no callers at all - the tag subsystem's missing middle - and is not: `DispatchAddTag` is an AWARD, so
		// re-awarding a held tag pops the client's banner again. The caller needs to know.
		public async Task<bool> AddCharacterTag(uint characterId, uint tagId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			if (await ctx.CharacterTags.AnyAsync(t => t.CharacterId == characterId && t.TagId == tagId))
				return false;

			ctx.CharacterTags.Add(new CharacterTag
			{
				CharacterId = characterId,
				TagId = tagId
			});
			await ctx.SaveChangesAsync();
			return true;
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

				// No equipment row to create: a naked character is a character with no Equipped item instances (#750),
				// which the spawn and equip paths read as the empty worn set it is. This used to seed ONE WIDE ROW of 26
				// empty slot columns purely so "a character has an equipment row" could be an invariant every reader
				// depended on, and half a dozen call sites threw on its absence. The representation no longer has an
				// absence to distinguish from emptiness.
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

		// The client-authored settings/preview blobs (#611), or null when the client has never uploaded either.
		public async Task<CharacterClientSettings?> GetCharacterClientSettings(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterClientSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CharacterId == characterId);
		}

		// Every stored blob on the account, for the reply to GetStartupData - the checksum map covers all characters and
		// the preview is sent per character, both before one has been picked.
		public async Task<CharacterClientSettings[]> GetCharacterClientSettingsByAccount(uint accountId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterClientSettings.AsNoTracking().Where(s => s.Character.AccountId == accountId).ToArrayAsync();
		}

		// Upsert of ONE half: a null argument leaves that column alone rather than clearing it, because the client
		// uploads the settings and the preview as separate messages and either can arrive without the other.
		public async Task SaveCharacterClientSettings(uint characterId, byte[]? settings, byte[]? preview)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterClientSettings.FirstOrDefaultAsync(s => s.CharacterId == characterId);
			if (row == null)
			{
				row = new CharacterClientSettings { CharacterId = characterId };
				ctx.CharacterClientSettings.Add(row);
			}

			row.Settings = settings ?? row.Settings;
			row.Preview = preview ?? row.Preview;
			await ctx.SaveChangesAsync();
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

		public async Task<IReadOnlyDictionary<CharacterEquipmentSlot, ItemInstance>> GetEquippedItems(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var worn = await ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets)
				.ThenInclude(x => x.Gem)
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped)
				.ToArrayAsync();

			// A worn item's Slot IS a CharacterEquipmentSlot. An UNDEFINED one is dropped rather than cast through: the
			// enum is the set of slots we can render, so an unknown slot on the wire would be a decode gap to report, not
			// something to guess at. (This is the same Enum.IsDefined gate the capture importer applies on the way in -
			// which is how VanityLegs (1016) was found, one item short at "20/21 worn".)
			return worn
				.Where(i => Enum.IsDefined((CharacterEquipmentSlot)i.Slot))
				.ToDictionary(i => (CharacterEquipmentSlot)i.Slot);
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

		// The spell-scoped stat modifiers the character's TRAINED feats carry (#593/#710) - "your Shock costs 10% less
		// mana", "every rank of your Frost line crits harder". The feat-sourced half of the one mechanism; the other
		// half rides live BUFFS and is read straight off the catalog (SpellSpellStatModifier).
		//
		// Two hops, for the same reason GetCharacterFeatEffects has two: CharacterFeats holds the WIRE feat id and the
		// modifier rows are keyed by the RDB TEMPLATE id, which FeatIdMaps bridges. The line -> rank fan-out is NOT
		// done here - the rows keep the target space the client stated, and SpellModifierService resolves it once
		// against the catalog, so a line row does not become N rows on every query.
		public async Task<FeatSpellStatModifier[]> GetCharacterFeatSpellStatModifiers(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
				join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
				join mod in ctx.FeatSpellStatModifiers on map.TemplateId equals mod.FeatId
				select mod).ToArrayAsync();
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
		public async Task<uint[]> GetFeatsTeachingSpells(IEnumerable<uint> spellIds)
		{
			var wanted = spellIds as IReadOnlyCollection<uint> ?? [.. spellIds];
			if (wanted.Count == 0)
				return [];

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var progressionTracks = ctx.FeatIdMaps.GroupBy(m => m.BaseIndex).Where(g => g.Max(m => m.Rank) >= FeatIdMap.MaxRank).Select(g => g.Key);

			return await (from grant in ctx.FeatSpellGrants.Where(g => wanted.Contains(g.SpellId))
				join map in ctx.FeatIdMaps on grant.FeatId equals map.TemplateId
				where !progressionTracks.Contains(map.BaseIndex)
				select map.WireFeatId).Distinct().ToArrayAsync();
		}

		/// <summary>
		///     The spells a set of just-TRAINED feats teaches - the inverse of
		///     <see cref="GetFeatsTeachingSpells" />, and the other half of the spell-grant story (#847).
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
		///         <see cref="FeatIdMap" /> to the RDB TEMPLATE id the grant is keyed by - the same two id spaces
		///         <see cref="GetFeatsTeachingSpells" /> bridges in the other direction (#467).
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

		public async Task<IReadOnlyDictionary<uint, int>> GetCharacterSpellBoosts(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Two hops. CharacterFeats holds the WIRE feat id and the modifier rows are keyed by the TEMPLATE id (a
			// different id space, hence FeatIdMaps); then the modifier names a spell LINE, which fans out to every
			// RANK of that spell. Resolving the fan-out HERE - once, at world entry - is what keeps the cast path a
			// dictionary lookup instead of a per-cast query.
			var rows = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
				join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
				join mod in ctx.FeatSpellModifiers on map.TemplateId equals mod.FeatId
				join spell in ctx.Spells on mod.SpellLineId equals spell.GuiStackingLine
				select new { spell.Id, mod.PercentHundredths }).ToArrayAsync();

			var totals = new Dictionary<uint, int>();
			foreach (var row in rows)
				totals[row.Id] = totals.GetValueOrDefault(row.Id) + row.PercentHundredths;

			return totals;
		}

		// What a character's trained feats do to their combos (#697). Two hops for the same reason
		// GetCharacterSpellBoosts has two: CharacterFeat.FeatId is a WIRE id and the modifier rows hang off the RDB
		// TEMPLATE id, which FeatIdMaps bridges.
		//
		// TWO tables, kept apart in the result because they are not the same statement: 53250 modifies one weapon
		// ATTACK of the combo (its opener or its finisher) and 53209 modifies the combo as a whole. Resolved here,
		// once at world entry, so the swing path stays a dictionary lookup.
		//
		// ⚠️ The two tables' units differ - 53250's are whole percents and 53209's hundredths - which is the client's
		// own doing (#593). Normalised to hundredths here so nothing downstream has to remember which is which.
		public async Task<IReadOnlyDictionary<uint, ComboFeatBonus>> GetCharacterComboBonuses(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var attacks = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
				join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
				join mod in ctx.FeatComboAttackModifiers on map.TemplateId equals mod.FeatId
				select new { mod.ComboId, mod.AttackSlot, mod.DpsPercent, mod.CriticalChanceRating }).ToArrayAsync();

			var upgrades = await (from feat in ctx.CharacterFeats.Where(f => f.CharacterId == characterId)
				join map in ctx.FeatIdMaps on feat.FeatId equals map.WireFeatId
				join mod in ctx.FeatComboModifiers on map.TemplateId equals mod.FeatId
				select new { mod.ComboId, mod.DamagePercentHundredths }).ToArrayAsync();

			var totals = new Dictionary<uint, ComboFeatBonus>();
			foreach (var row in attacks)
			{
				var bonus = totals.GetValueOrDefault(row.ComboId, ComboFeatBonus.None);
				totals[row.ComboId] = row.AttackSlot == ComboAttackSlot.Opening
					? bonus with
					{
						OpeningDamagePercentHundredths = bonus.OpeningDamagePercentHundredths + (row.DpsPercent * 100),
						OpeningCriticalRating = bonus.OpeningCriticalRating + row.CriticalChanceRating
					}
					: bonus with
					{
						FinishingDamagePercentHundredths = bonus.FinishingDamagePercentHundredths + (row.DpsPercent * 100),
						FinishingCriticalRating = bonus.FinishingCriticalRating + row.CriticalChanceRating
					};
			}

			foreach (var row in upgrades)
			{
				var bonus = totals.GetValueOrDefault(row.ComboId, ComboFeatBonus.None);
				totals[row.ComboId] = bonus with { SpellDamagePercentHundredths = bonus.SpellDamagePercentHundredths + row.DamagePercentHundredths };
			}

			return totals;
		}

		public async Task<IReadOnlyDictionary<uint, uint>> GetCharacterSpellScalingAttributes(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			// Only the character's OWN spells, and only the ones that actually carry an override - 716 of the 21,502
			// templates do, so a character typically has a handful or none, and the cast path stays a dictionary
			// lookup (the same reason GetCharacterSpellBoosts resolves its fan-out here).
			var rows = await (from known in ctx.CharacterSpells.Where(c => c.CharacterId == characterId)
				join spell in ctx.Spells on known.SpellId equals spell.Id
				where spell.ScalingAttribute != 0
				select new { spell.Id, spell.ScalingAttribute }).ToArrayAsync();

			return rows.ToDictionary(r => r.Id, r => r.ScalingAttribute);
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

		public async Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.WithSessionNavigations().FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
		}

		// `container` filters in SQL, like the quest reads' state: most callers want one container (the bag, the bank),
		// not everything the character holds. Null (the default) returns every item INCLUDING the worn set, which is
		// what the snapshot builder wants - all four of its containers now come from these rows.
		public async Task<ItemInstance[]> GetCharacterItems(uint characterId, ContainerKind? container = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ItemsOf(ctx)
				.Where(i => i.OwnerCharacterId == characterId && (container == null || i.Container == container))
				.OrderBy(i => i.Container)
				.ThenBy(i => i.Slot)
				.ToArrayAsync();
		}

		public async Task<ItemInstance?> GetCharacterItemSlot(uint characterId, ContainerKind container, int slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ItemsOf(ctx).FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot);
		}

		public async Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint? questId = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuestGoals
				.Where(g => g.CharacterId == characterId && (questId == null || g.QuestId == questId))
				.ToArrayAsync();
		}

		// `state` filters in SQL rather than in the caller: the goal-matching paths want only the ACTIVE quests, and a
		// level-80 character holds 1500+ completed ones they would otherwise materialise and discard on every call.
		// Null (the default) returns every held quest, which is what the journal and the escort lookup want.
		public async Task<CharacterQuest[]> GetCharacterQuests(uint characterId, QuestState? state = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && (state == null || q.State == state)).ToArrayAsync();
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

		public async Task<Achievement?> GetAchievement(uint tagId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Achievements.FirstOrDefaultAsync(a => a.Id == tagId);
		}

		public async Task<Achievement[]> FindAchievements(string nameSubstring, int limit)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var lowered = (nameSubstring ?? "").ToLowerInvariant();
			return await ctx.Achievements.Where(a => a.Name.ToLower().Contains(lowered)).OrderBy(a => a.Id).Take(limit).ToArrayAsync();
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
		// occupied slot (two identical rings -> the item twice), for server-side gear-stat aggregation (#3) - PLUS the
		// gems socketed into them (#311), one entry per socket. Reads the
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

		// The character's worn STAT-BEARING items (the 16 combat slots) with their StatModifiers loaded, one entry per
		// occupied slot (two identical rings -> the item twice), for server-side gear-stat aggregation (#3).
		//
		// #400: only the ACTIVE weapon SET's two slots are folded in - armour + jewellery always apply. A character can
		// have BOTH loadouts equipped at once (Primary = slots 6/8, Secondary = 20/21) and counting all four would
		// double-count. The two sets are just swappable loadouts, not inherently melee vs ranged.
		//
		// #585: the result is one entry per occupied SLOT, and the DISTINCT set is for the QUERY only - `IN (...)` cannot
		// use a duplicate, and loading the same template twice is just a second copy of one row. Returning the distinct
		// set silently HALVED every stat the character wore twice: their pools, their attribute scores, and (since #580)
		// the attribute-scaled part of every pool with them.
		public async Task<Item[]> GetEquippedStatItems(uint characterId, WeaponSet activeSet)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var wielded = activeSet == WeaponSet.Primary
				? new[] { CharacterEquipmentSlot.MainWeapon, CharacterEquipmentSlot.OffHandWeapon }
				: [CharacterEquipmentSlot.RangedWeapon, CharacterEquipmentSlot.Ammo];
			// The stat-bearing set minus the weapon slots of the loadout that is NOT drawn. Vanity slots are appearance
			// only and the Bag / ResourceBag containers carry a bag stat, not a combat one - neither is stat-bearing.
			var slots = CharacterEquipmentSlot.StatBearing
				.Where(slot => !IsWeaponSlot(slot))
				.Concat(wielded)
				.Select(slot => (int)slot)
				.ToArray();

			var wornInstances = await ctx.ItemInstances.AsNoTracking()
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped && slots.Contains(i.Slot))
				.Select(i => new
				{
					i.Id,
					i.TemplateId
				})
				.ToArrayAsync();
			if (wornInstances.Length == 0)
				return [];

			// #311: the GEMS socketed into those worn items count too, and they count through the ordinary item-stat
			// path - a socket carries no stat value in either wire form, so a gem's effect IS its own template's
			// modifier list. Returning them as extra entries is the whole of it, because the caller sums StatModifiers
			// across the array.
			//
			// One entry per SOCKET, exactly as the worn set is one entry per occupied slot: two of the same gem in two
			// sockets is that bonus twice. Distinct-ing here would halve it, which is the bug #585 fixed for the worn
			// items themselves.
			var wornIds = wornInstances.Select(i => i.Id).ToArray();
			var gems = await ctx.ItemInstanceSockets.AsNoTracking()
				.Where(s => wornIds.Contains(s.ItemInstanceId))
				.Select(s => s.GemItemId)
				.ToArrayAsync();

			var templates = wornInstances.Select(i => i.TemplateId).Concat(gems).ToArray();
			var ids = templates.Distinct().ToArray();
			var byId = await ctx.Items.AsNoTracking().Include(i => i.StatModifiers).Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id);
			return templates.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();

			static bool IsWeaponSlot(CharacterEquipmentSlot slot)
			{
				return slot is CharacterEquipmentSlot.MainWeapon or CharacterEquipmentSlot.OffHandWeapon or CharacterEquipmentSlot.RangedWeapon or CharacterEquipmentSlot.Ammo;
			}
		}

		// The (min, max) damage of the character's equipped MELEE main-hand weapon (MainWeapon), for melee resolution
		// (#395), or null if that slot is empty or the item carries no imported weapon damage. A lightweight projection -
		// no nav graph loaded (the combat swing hot path).
		public async Task<(int Min, int Max)?> GetEquippedWeaponDamage(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			const int mainWeapon = (int)CharacterEquipmentSlot.MainWeapon;
			var dmg = await ctx.ItemInstances
				.Where(i => i.OwnerCharacterId == characterId && i.Container == ContainerKind.Equipped && i.Slot == mainWeapon)
				.Join(ctx.Items, i => i.TemplateId, i => i.Id, (_, i) => new
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

		// A targeted single-column update rather than SaveCharacter: the client fires one of these per tutorial hint,
		// several per session, and there is no periodic character save that would pick up an in-memory change.
		public async Task SetCharacterTutorialSteps(uint characterId, ulong stepsSeen)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(c => c.SetProperty(x => x.TutorialStepsSeen, stepsSeen));
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

		public async Task SaveCharacterStats(uint characterId, IReadOnlyDictionary<CharacterStatId, uint> values)
		{
			if (values.Count == 0)
				return;

			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var ids = values.Keys.ToArray();
			var rows = await ctx.CharacterStats.Where(s => s.CharacterId == characterId && ids.Contains(s.StatId)).ToArrayAsync();
			foreach (var row in rows)
				row.Value = values[row.StatId];

			await ctx.SaveChangesAsync();
		}

		public async Task SaveCharacterDeadState(uint characterId, bool isDead)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDead, isDead));
		}

		// The one query shape every item read shares: the template (so a caller can name the item) plus the socketed
		// gems and THEIR templates (so a restored socket can name its gem). Split-query because two collection Includes
		// over one root is the cartesian-explosion shape (see the [[ef_split_query_for_multi_collection_includes]] note).
		private static IQueryable<ItemInstance> ItemsOf(DatabaseContext ctx)
		{
			return ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets)
				.ThenInclude(x => x.Gem);
		}

		public async Task SaveCharacterMount(uint characterId, uint? mountId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.MountId, mountId));
		}

		public async Task SaveCharacterScaffold(Character character)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			ctx.CharacterSkills.RemoveRange(ctx.CharacterSkills.Where(s => s.CharacterId == character.Id));
			ctx.CharacterScaffoldPairs.RemoveRange(ctx.CharacterScaffoldPairs.Where(p => p.CharacterId == character.Id));

			ctx.CharacterSkills.AddRange(character.Skills);
			ctx.CharacterScaffoldPairs.AddRange(character.ScaffoldPairs);

			// Feat SPECS are ENSURED, never delete+reinserted like the two above: CharacterFeat's foreign key is
			// (CharacterId, SpecIndex), so dropping a spec drops every feat trained in it - a scaffold save would
			// silently wipe a character's feats.
			//
			// This has to happen here because TrySeedFreshRows builds the fresh character's single unnamed spec IN
			// MEMORY and this is the only code that writes the scaffold. It didn't, so the spec was discarded at the
			// end of the create session; the next connection re-loaded the character with zero spec rows and the
			// spawn builder threw out of SpecsOf ("has no CharacterFeatSpec rows"), stranding the client on a
			// loading screen that never progressed. Every imported character has exactly one, so a character with
			// none was a shape no other code expected.
			var existingSpecs = await ctx.CharacterFeatSpecs.Where(s => s.CharacterId == character.Id).Select(s => s.Index).ToListAsync();
			foreach (var spec in character.FeatSpecs.Where(s => !existingSpecs.Contains(s.Index)))
				ctx.CharacterFeatSpecs.Add(new CharacterFeatSpec
				{
					CharacterId = character.Id,
					Index = spec.Index,
					Name = spec.Name
				});

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

		// `creatorName` is the crafter of the item being MINTED (#727). Defaulting it to null is what makes an ordinary
		// grant safe: an item arriving in a cell a crafted item used to occupy is not itself player-made, and carrying
		// the old value forward would glue the previous crafter to the new item - the same insert-only-writer shape that
		// froze stale Hash (#719) and Level (#725) values.
		//
		// ⚠️ This DELETES whatever occupied the cell, sockets included. That is right for a grant and wrong for a move -
		// a move must keep the instance, so it goes through MoveCharacterItem. Before #750 there was no difference to
		// draw: a "move" was a pair of writes that re-stated the id, the count and (by hand) the crafter into a new
		// address, and anything the code did not think to re-state was silently dropped. A gem is exactly that.
		public async Task<ItemInstance?> SetCharacterItemSlot(uint characterId, ContainerKind container, int slot, uint? templateId, uint count = 1, string? creatorName = null)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot);
			if (existing != null)
				ctx.ItemInstances.Remove(existing);

			if (templateId == null)
			{
				if (existing != null)
					await ctx.SaveChangesAsync();

				return null;
			}

			var minted = new ItemInstance
			{
				OwnerCharacterId = characterId,
				Container = container,
				Slot = slot,
				TemplateId = templateId.Value,
				Count = count,
				CreatorName = creatorName
			};
			ctx.ItemInstances.Add(minted);
			await ctx.SaveChangesAsync();

			// Load the Template the caller is going to need. Lazy loading is deliberately OFF, so a freshly minted
			// instance comes back with a null navigation while its sibling TransferCharacterItem Includes it - and
			// both feed the SAME live-add emitter, so the grant path would NRE on exactly the code path the hand-over
			// path exercises safely. Explicit load, per the DB conventions.
			await ctx.Entry(minted).Reference(i => i.Template).LoadAsync();

			return minted;
		}

		public async Task<IReadOnlyList<ItemInstance>> GetGuildBankItems(uint guildId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			return await ctx.ItemInstances.AsNoTracking()
				.Include(i => i.Template)
				.Include(i => i.Sockets).ThenInclude(x => x.Gem)
				.Where(i => i.OwnerGuildId == guildId && i.Container == ContainerKind.GuildBank)
				.OrderBy(i => i.Slot)
				.ToListAsync();
		}

		public async Task<bool> MoveGuildBankItem(uint characterId, uint guildId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot)
		{
			var intoVault = to == ContainerKind.GuildBank;
			if (intoVault == (from == ContainerKind.GuildBank))
				return false; // exactly one end is the vault; a bag-to-bag or vault-to-vault move is not this message

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var item = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Container == from && i.Slot == fromSlot &&
				(from == ContainerKind.GuildBank ? i.OwnerGuildId == guildId : i.OwnerCharacterId == characterId));
			if (item == null)
				return false;

			// ⚠️ No SWAP here, unlike MoveCharacterItem. Both captured moves land in a FREE cell, so what retail does
			// with an occupied one is unobserved - and a wrong guess costs somebody else's deposit. Refuse instead.
			var occupied = await ctx.ItemInstances.AnyAsync(i => i.Container == to && i.Slot == toSlot &&
				(intoVault ? i.OwnerGuildId == guildId : i.OwnerCharacterId == characterId));
			if (occupied)
				return false;

			item.Container = to;
			item.Slot = toSlot;
			item.OwnerCharacterId = intoVault ? null : characterId;
			item.OwnerGuildId = intoVault ? guildId : null;
			await ctx.SaveChangesAsync();

			return true;
		}

		public async Task<ItemInstanceSocket?> SocketGem(uint itemInstanceId, uint socketIndex, uint gemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var target = await ctx.ItemInstances.Include(i => i.Sockets).FirstOrDefaultAsync(i => i.Id == itemInstanceId);
			var gem = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Id == gemInstanceId);
			if (target == null || gem == null || target.Id == gem.Id)
				return null;

			// The unique key is (ItemInstanceId, SocketIndex), so a taken index would throw at SaveChanges with
			// nothing naming it. Refuse it here instead - the caller turns that into silence, which is what retail
			// does for a refused tradeskill.
			if (target.Sockets.Any(x => x.SocketIndex == socketIndex))
				return null;

			var socket = new ItemInstanceSocket
			{
				ItemInstanceId = target.Id,
				SocketIndex = socketIndex,
				GemItemId = gem.TemplateId
			};
			ctx.ItemInstanceSockets.Add(socket);

			// The gem is CONSUMED. A stack loses one; the last one takes the row with it.
			if (gem.Count > 1)
				gem.Count--;
			else
				ctx.ItemInstances.Remove(gem);

			await ctx.SaveChangesAsync();
			await ctx.Entry(socket).Reference(x => x.Gem).LoadAsync();

			return socket;
		}

		public async Task<bool> BindCharacterItem(uint itemInstanceId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var instance = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.Id == itemInstanceId);
			if (instance == null || instance.Bound)
				return false;

			instance.Bound = true;
			await ctx.SaveChangesAsync();

			return true;
		}

		// A move is a column UPDATE on the item's own row, which is the entire reason location lives on the instance.
		//
		// ⚠️ A SWAP cannot be one batch. Two rows trading (container, slot) is a CYCLE under the UNIQUE
		// (owner, container, slot) index - whichever UPDATE runs first collides with the row that has not moved yet -
		// and EF says so rather than picking an order ("a circular dependency was detected in the data to be saved").
		// SQLite would reject the intermediate state even if it did pick one: a unique index is enforced per statement,
		// there is nothing to defer to the end of the transaction.
		//
		// So the destination's occupant is PARKED at a slot no real cell uses, then the source moves in, then the parked
		// row takes the source's old cell - three statements, inside ONE transaction so a failure between them cannot
		// strand an item at the parking slot.
		public async Task<bool> MoveCharacterItem(uint characterId, ContainerKind from, int fromSlot, ContainerKind to, int toSlot)
		{
			if (from == to && fromSlot == toSlot)
				return true;

			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var source = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == from && i.Slot == fromSlot);
			if (source == null)
				return false;

			var destination = await ctx.ItemInstances.FirstOrDefaultAsync(i => i.OwnerCharacterId == characterId && i.Container == to && i.Slot == toSlot);
			if (destination == null)
			{
				source.Container = to;
				source.Slot = toSlot;
				await ctx.SaveChangesAsync();
				return true;
			}

			await using var transaction = await ctx.Database.BeginTransactionAsync();

			destination.Slot = ParkedSlot;
			await ctx.SaveChangesAsync();

			source.Container = to;
			source.Slot = toSlot;
			await ctx.SaveChangesAsync();

			destination.Container = from;
			destination.Slot = fromSlot;
			await ctx.SaveChangesAsync();

			await transaction.CommitAsync();
			return true;
		}

		// Where a swap parks the displaced item for the duration of the swap. Negative because every real cell position
		// is a wire slot index and those are non-negative, so it cannot collide with one; the transaction is what
		// guarantees nothing is ever observed sitting here.
		private const int ParkedSlot = -1;

		public async Task<ItemInstance?> TransferCharacterItem(uint instanceId, uint toCharacterId, ContainerKind container, int slot)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var instance = await ctx.ItemInstances.Include(i => i.Template).Include(i => i.Sockets).ThenInclude(x => x.Gem).FirstOrDefaultAsync(i => i.Id == instanceId);
			if (instance == null)
				return null;

			// Refuse rather than overwrite. The caller found a FREE cell, so an occupied one means the receiver's
			// inventory changed underneath us - and the alternative to refusing is destroying whatever is in it. The
			// UNIQUE (owner, container, slot) index would catch it anyway, as an exception at SaveChanges; catching it
			// here means the caller gets "it did not move" and the giver keeps the item.
			if (await ctx.ItemInstances.AnyAsync(i => i.OwnerCharacterId == toCharacterId && i.Container == container && i.Slot == slot))
				return null;

			instance.OwnerCharacterId = toCharacterId;
			instance.Container = container;
			instance.Slot = slot;
			await ctx.SaveChangesAsync();
			return instance;
		}

		public async Task<bool> SetCharacterItemCount(uint characterId, ContainerKind container, int slot, uint count)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.ItemInstances
				.Where(i => i.OwnerCharacterId == characterId && i.Container == container && i.Slot == slot)
				.ExecuteUpdateAsync(s => s.SetProperty(i => i.Count, count)) == 1;
		}

		public async Task SetCharacterMoney(uint characterId, ulong money)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Money, money));
		}

		// The kill/death counters, persisted immediately like SetCharacterProgress: they are earned one event at a
		// time, and a crash between the event and the autosave would silently lose the count.
		public async Task SetCharacterNpcKillCounts(uint characterId, uint kills, uint deaths)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.NpcKills, kills).SetProperty(c => c.NpcDeaths, deaths));
		}

		// See the interface note: the criminal debuff must outlive the session, so it is a persisted expiry rather
		// than session state.
		public async Task SetCharacterHeroicMode(uint characterId, uint heroicMode)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.HeroicMode, heroicMode));
		}

		public async Task SetCharacterCriminalState(uint characterId, DateTimeOffset? criminalUntil, uint murderPoints)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.CriminalUntil, criminalUntil).SetProperty(c => c.MurderPoints, murderPoints));
		}

		public async Task SetCharacterProgress(uint characterId, uint experience, ushort level)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Experience, experience).SetProperty(c => c.Level, level));
		}

		// Record which pick-one-of-N reward the character took for a quest (#815). Writing it is what makes the grant
		// idempotent - the selection is an ordinary c2s packet and can arrive any number of times.
		public async Task SetCharacterQuestReward(uint characterId, uint questId, uint chosenPos)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.CharacterQuests.Where(q => q.CharacterId == characterId && q.QuestId == questId)
				.ExecuteUpdateAsync(s => s.SetProperty(q => q.RewardChosenPos, chosenPos));
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
			var character = await ctx.Characters.FirstOrDefaultAsync(c => c.Id == characterId);
			if (character == null || !character.SetVanityFilter(slot, toggleState))
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
				.Include(c => c.Skills)
				.Include(c => c.Feats)
				.Include(c => c.FeatSpecs)
				.Include(c => c.ScaffoldPairs);
		}
	}
}

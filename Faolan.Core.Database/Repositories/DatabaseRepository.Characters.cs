using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
	{
		Task<int> AddCharacterFeats(uint characterId, IEnumerable<uint> featIds);
		Task<bool> AddCharacterSpell(uint characterId, uint spellId);
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
		Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<CharacterInventory[]> GetCharacterInventory(uint characterId);
		Task<CharacterQuestGoal[]> GetCharacterQuestGoals(uint characterId, uint questId);
		Task<CharacterQuest[]> GetCharacterQuests(uint characterId);
		Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true);
		Task<Character?> GetCharacterScaffold(uint characterId);
		Task<CharacterShortcut[]> GetCharacterShortcuts(uint characterId);
		Task<CharacterSpell[]> GetCharacterSpells(uint characterId);
		Task<CharacterStat[]> GetCharacterStats(uint characterId);
		Task<uint[]> GetCharacterTags(uint characterId);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);
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
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank);
		Task SetCharacterInventorySlot(uint characterId, ContainerKind container, int slot, uint? itemId, uint count = 1);
		Task SetCharacterMoney(uint characterId, ulong money);
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
		// Character ids ARE the on-wire client instance and the burst replay substitutes them into captured 4-byte
		// protobuf-varint slots, so they must encode to <= 4 varint bytes (id <= 0x0FFFFFFF). Allocate from this base
		// continuing the existing 0x013xxxxx rows; a 5-byte id (> 0x0FFFFFFF) crashes the client (see DatabaseContext).
		private const uint FirstCharacterId = 0x02000000;
		private const uint MaxCharacterId = 0x0FFFFFFF;

		public async Task<int> AddCharacterFeats(uint characterId, IEnumerable<uint> featIds)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var existing = await ctx.CharacterFeats.Where(f => f.CharacterId == characterId).ToListAsync();
			var have = existing.Select(f => f.FeatId).ToHashSet();
			var nextSeq = existing.Count == 0
				? 0
				: existing.Max(f => f.Sequence) + 1;
			var added = 0;
			foreach (var id in featIds)
			{
				if (!have.Add(id))
					continue;

				ctx.CharacterFeats.Add(new CharacterFeat
				{
					CharacterId = characterId,
					FeatId = id,
					Sequence = nextSeq++
				});
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
					AuthChallenge = Guid.NewGuid().ToString("N"),
					Level = 1,
					Class = Class.Barbarian,
					Race = Race.Cimmerian,
					CreationDate = DateTimeOffset.UtcNow,
					MapId = Map.NewPlayerSpawnSpawnPoint.MapId,
					Position = Map.NewPlayerSpawnSpawnPoint.Position
				};

				character.MaxStats = CharacterMaxStats.CreateDefaults(character.Class, character.Level);

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
			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.Id == id);
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

		public async Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			var directMatch = await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
			if (directMatch != null)
				return directMatch;

			return await ctx.Characters.Include(c => c.Body).Include(c => c.MaxStats).Where(c => c.AccountId == accountId).OrderByDescending(c => c.Id).FirstOrDefaultAsync();
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

		public async Task<Character[]> GetCharactersByAccount(uint id, bool skipUninitialized = true)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var q = ctx.Characters.Where(a => a.AccountId == id);
			if (skipUninitialized)
				q = q.Where(c => c.Name != null);

			return await q.ToArrayAsync();
		}

		public async Task<Character?> GetCharacterScaffold(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx
				.Characters.Include(c => c.Attributes)
				.Include(c => c.Skills)
				.Include(c => c.Feats)
				.Include(c => c.ScaffoldPairs)
				.Include(c => c.Body)
				.Include(c => c.BodyResources)
				.Include(c => c.Equipment)
				.FirstOrDefaultAsync(c => c.Id == characterId);
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

		public async Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterRespawnPoints.Where(c => c.CharacterId == characterId).Select(c => c.RespawnPointId).ToArrayAsync();
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
				.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s.SetProperty(c => c.MapId, Map.NewPlayerSpawnSpawnPoint.MapId).SetProperty(c => c.Position, Map.NewPlayerSpawnSpawnPoint.Position)) == 1;
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
			return await ctx
				.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s.SetProperty(a => a.LastConnection, DateTimeOffset.UtcNow).SetProperty(a => a.LastIpAddress, ipAddress)) == 1;
		}
	}
}

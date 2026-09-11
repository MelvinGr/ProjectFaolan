using System;
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
	// The character row and its 1:1 satellites: create/load/save, the transform and bind, respawn discovery, the
	// kill counters, client settings, friends and the achievement tags. What a character is, as opposed to what it
	// carries (ICharacterInventoryRepository), is on (ICharacterQuestRepository), has learned
	// (ICharacterProgressionRepository) or adds up to (ICharacterStatRepository).
	public interface ICharacterRepository
	{
		Task<bool> AddFriend(uint characterId, uint friendCharacterId);
		Task<Character[]> GetFriends(uint characterId);
		Task<uint[]> GetFriendWatchers(uint characterId);
		Task<bool> RemoveFriend(uint characterId, uint friendCharacterId);
		Task<Character> CreateCharacter(uint accountId, uint realmId);
		Task<bool> DeleteCharacter(uint characterId);
		Task DiscoverRespawnPoint(uint characterId, uint respawnPointId);
		Task<Character?> GetCharacter(uint id);
		Task<CharacterClientSettings?> GetCharacterClientSettings(uint characterId);
		Task<CharacterClientSettings[]> GetCharacterClientSettingsByAccount(uint accountId);
		Task SaveCharacterClientSettings(uint characterId, byte[]? settings, byte[]? preview);
		Task<Character?> GetCharacterByName(string name, uint realmId);

		/// <summary>
		///     The client's forbidden-word list for character names, as `NameFilterTerm` rows (hash + length). Empty
		///     when `rdb-name-filter` has not been run, which leaves `CharacterNameRules` enforcing length only.
		/// </summary>
		Task<NameFilterTerm[]> GetNameFilterTerms();
		Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance);
		Task<uint[]> GetCharacterTags(uint characterId);

		// Grant a tag (an achievement, or an item-shop "bundle shown" flag - one id space). False = already held.
		Task<bool> AddCharacterTag(uint characterId, uint tagId);

		// The achievement a tag id names, or null for an id the client's catalog does not carry (LDB 55701).
		Task<Achievement?> GetAchievement(uint tagId);

		// Achievements whose name contains `nameSubstring`, capped at `limit` - a 4,095-entry catalog needs a lookup.
		Task<Achievement[]> FindAchievements(string nameSubstring, int limit);
		Task<Character[]> GetCharactersByAccount(uint id);
		Task<uint[]> GetDiscoveredRespawnPointIds(uint characterId);
		Task SaveCharacter(Character character);
		Task SaveCharacterBind(uint characterId, uint respawnPointId);
		Task SaveCharacterDeadState(uint characterId, bool isDead);
		Task SaveCharacterMount(uint characterId, uint? mountId);
		Task SaveCharacterScaffold(Character character);
		Task SaveCharacterTransform(uint characterId, Vector3 position, Quaternion rotation, uint mapId);

		/// <summary>
		///     Persist (or with a null <paramref name="mapId" /> clear) the character's PvP-match return location -
		///     where the minigame teleport took them from. Consumed by the world entry when the match is gone.
		/// </summary>
		Task SaveCharacterPvpReturn(uint characterId, uint? mapId, Vector3 position);
		Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank);
		Task SetCharacterNpcKillCounts(uint characterId, uint kills, uint deaths);

		/// <summary>The PvP career counters (snapshot stats 0x290 / 0x292), persisted per kill like the PvE pair.</summary>
		Task SetCharacterPvpKillCounts(uint characterId, uint kills, uint deaths);

		// The PvP criminal state: when the criminal debuff expires (null = not a criminal) and the murder-point
		// pool. Persisted immediately, like the kill counters - the debuff has to survive a relog and a zone change,
		// which is what the annotated capture shows it doing.
		Task SetCharacterCriminalState(uint characterId, DateTimeOffset? criminalUntil, uint murderPoints);

		/// <summary>Stat 0x0A9 `HeroicMode` - the raw wire value, see Character.HeroicMode.</summary>
		Task SetCharacterHeroicMode(uint characterId, uint heroicMode);

		/// <summary>Records the Tortage tutorial hints a character has been shown, as a bitmask.</summary>
		Task SetCharacterTutorialSteps(uint characterId, ulong stepsSeen);
		Task<bool> SetCreationSpawn(uint characterId);
		Task<bool> SetVanityFilter(uint characterId, uint slot, bool toggleState);
		Task<bool> UpdateLastInfo(uint characterId, string ipAddress);
	}

	public partial class DatabaseRepository
	{
		private const uint MaxCharacterId = EntityHandles.MaxCharacterId;

		// Character ids are the on-wire client instance (see EntityHandles for the whole number line and why the
		// ceiling exists). Allocated from the base upward, continuing the existing 0x013xxxxx rows.
		private const uint FirstCharacterId = EntityHandles.CharacterInstanceBase;

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

		// The characters who have `characterId` on their list - the reverse direction, which the one-sided list makes a
		// genuinely different query. Needed to tell them when this character comes online or goes offline; without it a
		// friend's status is frozen at whatever it was when the watcher logged in.
		public async Task<uint[]> GetFriendWatchers(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.CharacterFriends.Where(f => f.FriendCharacterId == characterId).Select(f => f.CharacterId).ToArrayAsync();
		}

		// The befriended characters, not the join rows - every caller needs the name and class to build the wire record.
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

		// Returns false when the character already held the tag. The caller needs to know: `DispatchAddTag` is an
		// award, so re-awarding a held tag pops the client's banner again.
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

				// No equipment row to create: a naked character is a character with no Equipped item instances,
				// which the spawn and equip paths read as the empty worn set it is. The representation has no absence
				// to distinguish from emptiness, so there is nothing to pre-seed.
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

		public async Task<Character?> GetCharacter(uint id)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.WithSessionNavigations().FirstOrDefaultAsync(c => c.Id == id);
		}

		// The client-authored settings/preview blobs, or null when the client has never uploaded either.
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

		// Upsert of one half: a null argument leaves that column alone rather than clearing it, because the client
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

		public async Task<NameFilterTerm[]> GetNameFilterTerms()
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.NameFilterTerms.AsNoTracking().ToArrayAsync();
		}

		public async Task<Character?> GetCharacterForAccount(uint accountId, uint clientInstance)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.WithSessionNavigations().FirstOrDefaultAsync(c => c.AccountId == accountId && c.Id == clientInstance);
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

		// The account's playable characters - a nameless row is a create in progress, never something to list. That was
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

		// A targeted single-column update rather than SaveCharacter: the client fires one of these per tutorial hint,
		// several per session, and there is no periodic character save that would pick up an in-memory change.
		public async Task SetCharacterTutorialSteps(uint characterId, ulong stepsSeen)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(c => c.SetProperty(x => x.TutorialStepsSeen, stepsSeen));
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

		public async Task SaveCharacterDeadState(uint characterId, bool isDead)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDead, isDead));
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

			// Feat specs are ensured, never delete+reinserted like the two above: CharacterFeat's foreign key is
			// (CharacterId, SpecIndex), so dropping a spec drops every feat trained in it - a scaffold save would
			// silently wipe a character's feats.
			//
			// This has to happen here because TrySeedFreshRows builds the fresh character's single unnamed spec in
			// memory and this is the only code that writes the scaffold. It didn't, so the spec was discarded at the
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

		public async Task SaveCharacterPvpReturn(uint characterId, uint? mapId, Vector3 position)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx
				.Characters.Where(c => c.Id == characterId)
				.ExecuteUpdateAsync(s => s
					.SetProperty(c => c.PvpReturnMapId, mapId)
					.SetProperty(c => c.PvpReturnX, position.X)
					.SetProperty(c => c.PvpReturnY, position.Y)
					.SetProperty(c => c.PvpReturnZ, position.Z));
		}

		public async Task<bool> SetCharacterGuild(uint characterId, uint? guildId, GuildRank rank)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.GuildId, guildId).SetProperty(c => c.GuildRank, rank)) == 1;
		}

		// The kill/death counters, persisted immediately like SetCharacterProgress: they are earned one event at a
		// time, and a crash between the event and the autosave would silently lose the count.
		public async Task SetCharacterNpcKillCounts(uint characterId, uint kills, uint deaths)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.NpcKills, kills).SetProperty(c => c.NpcDeaths, deaths));
		}

		// The PvP career pair (0x290/0x292), persisted per event for the same reason as the PvE one above.
		public async Task SetCharacterPvpKillCounts(uint characterId, uint kills, uint deaths)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await ctx.Characters.Where(c => c.Id == characterId).ExecuteUpdateAsync(s => s.SetProperty(c => c.PvpKills, kills).SetProperty(c => c.PvpDeaths, deaths));
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
		// that is not included here reads as empty rather than failing - which makes the include list part of the
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

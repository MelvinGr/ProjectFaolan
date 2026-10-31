using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// The Khitai faction system's data access - the catalog (read-only, from the client) and one
	// character's standing (play state).
	public interface IFactionRepository
	{
		/// <summary>
		///     What killing one NPC of <paramref name="victimFactionId" /> at <paramref name="tier" /> is worth, per
		///     faction it moves. Empty when the victim belongs to no faction, or to one with no entry at that tier.
		/// </summary>
		Task<IReadOnlyList<FactionKillAward>> GetFactionKillAwards(uint victimFactionId, int tier);

		/// <summary>
		///     One character's standings, by faction id. A faction with no row is neutral, not unknown - the row is
		///     created the first time the standing moves, so this never returns sixteen zeroes.
		/// </summary>
		Task<IReadOnlyDictionary<uint, int>> GetCharacterFactionStandings(uint characterId);

		/// <summary>Writes one character's standing with one faction, inserting the row if it does not exist yet.</summary>
		Task SetCharacterFactionStanding(uint characterId, uint factionId, int points);
	}

	public partial class DatabaseRepository
	{
		public async Task<IReadOnlyList<FactionKillAward>> GetFactionKillAwards(uint victimFactionId, int tier)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			return await ctx.FactionKillAwards.AsNoTracking()
				.Where(a => a.VictimFactionId == victimFactionId && a.Tier == tier)
				.ToListAsync();
		}

		public async Task<IReadOnlyDictionary<uint, int>> GetCharacterFactionStandings(uint characterId)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();

			return await ctx.CharacterFactionStandings.AsNoTracking()
				.Where(s => s.CharacterId == characterId)
				.ToDictionaryAsync(s => s.FactionId, s => s.Points);
		}

		public async Task SetCharacterFactionStanding(uint characterId, uint factionId, int points)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			var row = await ctx.CharacterFactionStandings
				.FirstOrDefaultAsync(s => s.CharacterId == characterId && s.FactionId == factionId);

			if (row == null)
			{
				ctx.CharacterFactionStandings.Add(new CharacterFactionStanding
				{
					CharacterId = characterId,
					FactionId = factionId,
					Points = points
				});
			}
			else
			{
				row.Points = points;
			}

			await ctx.SaveChangesAsync();
		}
	}
}

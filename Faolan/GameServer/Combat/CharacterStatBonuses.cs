using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The additive stat bonuses a character carries into the world, from every source that is not the class/level
	///     BASE: equipped GEAR (#3), the feats they TRAINED (#251) and the feats the client trains for them (#863).
	/// </summary>
	/// <remarks>
	///     One place rather than three. World entry, the post-equip refresh and the max-stat reseed each need the
	///     identical aggregate, and each used to build it inline - so a fourth source could be (and was) added to one
	///     and not the others. The class BASE is deliberately NOT folded in here: it is not a bonus, it is what the
	///     bonuses are added TO, and <c>EntityVitals</c> takes it as its own argument.
	/// </remarks>
	public static class CharacterStatBonuses
	{
		public static async Task<IReadOnlyDictionary<CharacterStatId, int>> For(IDatabaseRepository database, Character character)
		{
			return MaxStatAggregator.Combine(
				MaxStatAggregator.AggregateGearBonuses(await database.GetEquippedStatItems(character.Id, character.ActiveWeaponSet)),
				MaxStatAggregator.AggregateFeatBonuses(await database.GetCharacterFeatEffects(character.Id), character.Level),
				// ⭐ The AUTO-TRAINED half (#863). These feats are in no character's list by design - retail never puts
				// one on the wire - so they are derived from (class, level) exactly as the client derives them, and
				// they are the source of the mana-regen base and the skill scores nothing else states.
				MaxStatAggregator.AggregateFeatBonuses(
					await database.GetClassAutoTrainedFeatEffects(character.Class, character.Level), character.Level));
		}
	}
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Faolan.Importer.Rdb
{
	// The pet a summon spell spawns, read out of the ScriptParams string of its `StartServerSideScryScriptWithParams`
	// (53261) GameAction element:
	//
	//   spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781
	//
	// A `key=value;key=value` list, order not relied on. This is what makes the summon roster a catalog read rather
	// than a hand-typed table: `spawnHash` present == this spell summons a pet, over all 21,502 spell records.
	//
	// 53261 is a general "run this server script" action, so the action does not mark a summon - the params do. It
	// runs unrelated scripts elsewhere in the catalog, which is why a summon is recognised by its params.
	//
	// There are two spellings, and which one a spell uses is what says whether the pet fights:
	//   spawnHash=YU86;pointCost=2;petBuff=1366140;spellID=385781     a combat pet
	//   Type=Pet;Tag=3681                                              the social companion
	// Every captured summon agrees: all 11 spawnHash pets bind with PetBindings.CombatKind and the one Type=Pet pet
	// with SocialKind (docs/subsystems/summon-pet-decode.md). So `Companion` is read off the spelling rather than
	// carried as a third fact.
	//
	// `spawnHash` is a 4-character ACG token that resolves to nothing in the client - it addressed retail's
	// server-side content (see Spell.SummonSpawnHash). It is kept as the pet's stable identity, not as a lookup key.
	public sealed record SummonParams(string Token, bool Companion, uint? PetBuff, uint? PointCost)
	{
		private const string SpawnHashKey = "spawnHash";
		private const string PetBuffKey = "petBuff";
		private const string PointCostKey = "pointCost";
		private const string TypeKey = "Type";
		private const string TagKey = "Tag";
		private const string PetType = "Pet";

		// Returns false - not a throw - for every string that is not a pet summon, which is the common case: most
		// 53261 elements in the catalog run some other script, and a caller sweeping the whole catalog is asking
		// "is this one?" rather than decoding a known-good body.
		public static bool TryParse(string? scriptParams, [NotNullWhen(true)] out SummonParams? summon)
		{
			summon = null;
			if (string.IsNullOrEmpty(scriptParams))
				return false;

			string? spawnHash = null, tag = null;
			var isPetType = false;
			uint? petBuff = null, pointCost = null;
			foreach (var pair in scriptParams.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				var split = pair.IndexOf('=');
				if (split <= 0)
					continue;

				var key = pair[..split];
				var value = pair[(split + 1)..];
				if (key.Equals(SpawnHashKey, StringComparison.OrdinalIgnoreCase))
					spawnHash = value;
				else if (key.Equals(PetBuffKey, StringComparison.OrdinalIgnoreCase) && uint.TryParse(value, CultureInfo.InvariantCulture, out var buff))
					petBuff = buff;
				else if (key.Equals(PointCostKey, StringComparison.OrdinalIgnoreCase) && uint.TryParse(value, CultureInfo.InvariantCulture, out var cost))
					pointCost = cost;
				else if (key.Equals(TypeKey, StringComparison.OrdinalIgnoreCase))
					isPetType = value.Equals(PetType, StringComparison.OrdinalIgnoreCase);
				else if (key.Equals(TagKey, StringComparison.OrdinalIgnoreCase))
					tag = value;
			}

			if (!string.IsNullOrEmpty(spawnHash))
			{
				summon = new SummonParams(spawnHash, false, petBuff, pointCost);
				return true;
			}

			if (!isPetType || string.IsNullOrEmpty(tag))
				return false;

			summon = new SummonParams(tag, true, petBuff, pointCost);
			return true;
		}
	}
}

using System.Collections.Generic;

namespace Faolan.GameServer.World
{
	// One summonable mount: the bar/feat id the client casts (SummonMount action `from`), the creature it spawns
	// (an FA015807 NPC, matched by NAME against the harvested mount spawns - the stable key), and the summon SPELL
	// the server casts in response (ApplySpell 0x6B87E316). Decoded from the necrol80 mount captures (#27).
	public sealed record MountDef(uint Id, string CreatureName, uint SummonSpellId);

	// The summonable-mount catalog (#27). The player triggers a mount with a BehaviorControlCenter SummonMount
	// action (ActionType 0xA) whose `from` is the mount id; the server answers by casting the summon spell and
	// spawning the mount creature at the player. Capture-grounded from the NecroL80 mount/dismount captures:
	//   - SummonMount `from` 0x3E34F1 = "Fading Black Horse", 0x3E34F2 = "Swift Fading Black Horse"
	//     (Handle0x2000 SummonMount comments + the 0x2076 action-bar entries).
	//   - the summon cast is ApplySpell spell 0x3BAD7D (the channelled 0xF508F4C1 summon that precedes the
	//     "Fading Black Horse" FA015807 spawn; the `.mount` GM command's documented id).
	// Typed catalog rather than a captured blob: the id/name/spell are the only per-mount facts and they are tiny
	// + capture-proven (construct-don't-replay). The mount CREATURE model is harvested into the DB by
	// CaptureMountImporter and rendered via the normal NPC-spawn path (SpawnMountForPlayer).
	public static class MountCatalog
	{
		// The captured mount-summon spell (channelled ApplySpell 0x6B87E316 / spell 0x3BAD7D). Both fading-horse
		// variants use it; the swift variant's own id is not separately captured, so it shares this summon.
		public const uint FadingHorseSummonSpell = 0x3BAD7Du;

		public static readonly IReadOnlyList<MountDef> All =
		[
			new MountDef(0x3E34F1u, "Fading Black Horse", FadingHorseSummonSpell),
			new MountDef(0x3E34F2u, "Swift Fading Black Horse", FadingHorseSummonSpell)
		];

		// The default mount the GM `.mount` command summons when given no argument (the in-client-observed Tortage mount).
		public static MountDef Default => All[0];
		private static readonly Dictionary<uint, MountDef> ById = BuildById();
		private static readonly Dictionary<string, MountDef> ByName = BuildByName();

		public static MountDef ForId(uint id)
		{
			return ById.GetValueOrDefault(id);
		}

		public static MountDef ForName(string name)
		{
			return name != null && ByName.TryGetValue(name.ToLowerInvariant(), out var m)
				? m
				: null;
		}

		private static Dictionary<uint, MountDef> BuildById()
		{
			var map = new Dictionary<uint, MountDef>();
			foreach (var m in All)
				map[m.Id] = m;
			return map;
		}

		private static Dictionary<string, MountDef> BuildByName()
		{
			var map = new Dictionary<string, MountDef>();
			foreach (var m in All)
				map[m.CreatureName.ToLowerInvariant()] = m;
			return map;
		}
	}
}

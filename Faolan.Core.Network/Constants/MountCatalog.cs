using System.Collections.Generic;
using Faolan.Core.Network.Codecs.Combat;

namespace Faolan.Core.Network.Constants
{
	// The summonable-mount catalog (#27). The player triggers a mount with a BehaviorControlCenter SummonMount
	// action (ActionType 0xA) whose `from` is the mount id; the server answers by casting the summon spell and
	// spawning the mount creature at the player. Capture-grounded from the NecroL80 mount/dismount captures:
	//   - SummonMount `from` 0x3E34F1 = "Fading Black Horse", 0x3E34F2 = "Swift Fading Black Horse"
	//     (Handle0x2000 SummonMount comments + the 0x2076 action-bar entries).
	//   - the summon cast is ApplySpell spell 0x3BAD7D (the channelled 0xF508F4C1 summon that precedes the
	//     "Fading Black Horse" FA015807 spawn).
	// Typed catalog rather than a captured blob: the id/name/spell are the only per-mount facts and they are tiny
	// + capture-proven (construct-don't-replay). The mount CREATURE model is harvested into the DB by
	// CaptureMountImporter and rendered via the normal NPC-spawn path (SpawnMountForPlayer).
	public static class MountCatalog
	{
		// The summon CHANNEL: retail casts the summon spell, then applies the ride buff ~545ms later (mount capture
		// SummonMount frame 498 -> ride buff frame 520 = 0.545s), during which the client plays the horse-runs-up
		// animation. Applying the ride buff instantly (as we did) mounts with NO visible run-up. (The DB spell 0x3BAD7D
		// is mislabelled "Stagger" with CastTime 0, so this comes from the capture, not the Spell row.)
		public const int SummonChannelMs = 545;

		// The DISMOUNT: the ride buff's end twin (rank 01, empty payload) - removes the mounted state. Pressing the
		// mount button while mounted toggles it; retail runs the SAME channel (cast bar + horse-runs-away animation)
		// then applies this end-twin + a StatUpd clearing the mount stat (0xBA=0). Mount-dismount capture frame 1449.
		public const uint DismountSpell = 0x31EB1501u;

		// The captured mount-summon spell (channelled ApplySpell 0x6B87E316 / spell 0x3BAD7D). Both fading-horse
		// variants use it; the swift variant's own id is not separately captured, so it shares this summon.
		public const uint FadingHorseSummonSpell = 0x3BAD7Du;

		// The RIDE buff (#26): after the horse spawns, retail applies a second SELF ApplySpell - spell base 0x31EB15,
		// apply rank 00 - the mounted/riding state; the dismount is its end twin 0x31EB1501 with an empty payload.
		// Fully captured in NecroL80_..._login_mount_move_dismount (apply frame 520 / end frame 1449). The summon alone
		// spawns the horse but never seats the player (proven in-client 2026-06-17).
		public const uint RideSpell = 0x31EB1500u;

		// The captured ride-buff facts (the DB Spell row for 0x31EB15 is mislabelled, so these come from the mount
		// capture, frame 520): a 24 h duration and the per-application effect nonce. The payload itself is the 54-byte
		// MagicBuffIIR shape SpellweaveCodec builds (the client wants that shape here, not the 50-byte self-buff form).
		private const uint RideBuffDurationCentiseconds = 8_640_000; // 24 h
		private const uint RideBuffEffectNonce = 0x1560F9BBu;

		public static readonly IReadOnlyList<MountDef> All =
		[
			new MountDef(0x3E34F1u, "Fading Black Horse", FadingHorseSummonSpell, 0x00313C9Bu), new MountDef(0x3E34F2u, "Swift Fading Black Horse", FadingHorseSummonSpell, 0x00313C9Bu)
		];

		// The default mount summoned when no specific mount is given (the in-client-observed Tortage mount).
		public static MountDef Default => All[0];
		private static readonly Dictionary<uint, MountDef> ById = BuildById();
		private static readonly Dictionary<string, MountDef> ByName = BuildByName();

		public static MountDef? ForId(uint id)
		{
			return ById.GetValueOrDefault(id);
		}

		public static MountDef? ForName(string name)
		{
			return name != null && ByName.TryGetValue(name.ToLowerInvariant(), out var m)
				? m
				: null;
		}

		// The mount RIDE buff's 54-byte MagicBuffIIR payload (the generic LONG form), CONSTRUCTED from its base spell
		// id + captured duration and nonce (byte-identical to the mount-capture frame 520, proven by MountCatalogTests).
		public static byte[] RideBuffPayload()
		{
			return SelfBuffCodec.BuildBuffDataLong(RideSpell >> 8, RideBuffDurationCentiseconds, RideBuffEffectNonce);
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

	// One summonable mount: the bar/feat id the client casts (SummonMount action `from`), the creature it spawns
	// (an FA015807 NPC, matched by NAME against the harvested mount spawns - the stable key), and the summon SPELL
	// the server casts in response (ApplySpell 0x6B87E316). Decoded from the NecroL80 mount captures (#27).
	// RideStat = the mount's value for player stat 0xBA in the mount StatUpd retail pushes alongside the ride buff
	// (mount capture frame 520: stat 0xBA = 0x00313C9B, stat 0xBB = 100). It selects the mount the player is riding;
	// paired with the ride buff it's what the CLIENT animates the whole horse-runs-up-and-mount from - retail spawns
	// NO server horse entity.
	public sealed record MountDef(uint Id, string CreatureName, uint SummonSpellId, uint RideStat);
}

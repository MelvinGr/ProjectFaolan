using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Xunit;

namespace Faolan.Tests
{
	// The live in-memory world model (Gitea #404): per-map zones, an entity registry, and
	// the spatial range queries interest management uses to send moves/spawns to nearby players only.
	public class WorldStateTests
	{
		[Fact]
		public void EntitiesInRange_IncludesNpcs_WithinRange()
		{
			var zone = new Zone(2040);
			zone.Add(Player(1, 0, 0));
			zone.Add(NpcTemplate(4, 5, 0));
			zone.Add(NpcTemplate(5, 100, 0));

			var seen = zone.EntitiesInRange(new Vector3(0, 0, 0), 50f).Select(e => e.Handle).OrderBy(h => h).ToArray();

			Assert.Equal(new uint[] { 1, 4 }, seen); // self + near npc; the far npc is out of range
		}

		[Fact]
		public void NextPlayfieldToken_IsMonotonic()
		{
			var world = new WorldState();
			Assert.True(world.NextPlayfieldToken() < world.NextPlayfieldToken());
		}

		[Fact]
		public void Place_AddsToMapZone_AndRelocatesOnMapChange()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);

			world.Place(e, 2040, new Vector3(10, 0, 10), Quaternion.Identity);
			Assert.Equal(2040u, e.MapId);
			Assert.Equal(1, world.ZoneFor(2040).Count);

			// A map change moves the entity out of the old zone into the new one.
			world.Place(e, 3000, new Vector3(5, 0, 5), Quaternion.Identity);
			Assert.Equal(0, world.ZoneFor(2040).Count);
			Assert.Equal(1, world.ZoneFor(3000).Count);
			Assert.True(world.ZoneFor(3000).TryGet(1, out _));
		}

		[Fact]
		public void PlayersInRange_ReturnsNearbyPlayers_ExcludesSelfFarAndNpcs()
		{
			var zone = new Zone(2040);
			var self = Player(1, 0, 0);
			var near = Player(2, 30, 0); // within 50
			var far = Player(3, 200, 0); // outside 50
			var npc = NpcTemplate(4, 5, 0); // close but not a player
			foreach (var e in new[] { self, near, far, npc })
				zone.Add(e);

			var seen = zone.PlayersInRange(self.Position, 50f, self.Handle).Select(e => e.Handle).OrderBy(h => h).ToArray();

			Assert.Equal(new uint[] { 2 }, seen); // only the near player; self excluded, far out of range, npc filtered
		}

		[Fact]
		public void Remove_TakesEntityOutOfItsZone()
		{
			var world = new WorldState();
			var e = Player(1, 0, 0);
			world.Place(e, 2040, Vector3.Zero, Quaternion.Identity);

			world.Remove(e);

			Assert.Equal(0, world.ZoneFor(2040).Count);
		}

		[Fact]
		public void ResolveEntryInstance_KeepsCopyOnReentry_NewCopyOnMapChange( /* #268 */)
		{
			var world = new WorldState();
			var solo = world.ResolveEntryInstance(null, 4010, MapInstanceMode.Solo);
			var p = Player(1, 0, 0);
			world.Place(p, 4010, solo, p.Position, Quaternion.Identity);

			// Same-map re-entry (respawn/teleport back) keeps the player's existing copy.
			Assert.Equal(solo, world.ResolveEntryInstance(p, 4010, MapInstanceMode.Solo));
			// A different map mints a fresh copy (the old instance id doesn't carry across maps).
			Assert.NotEqual(solo, world.ResolveEntryInstance(p, 5000, MapInstanceMode.Solo));
		}

		// #309: an EPIC entry never shares a zone with normal players, and the two modes of one map are two copies.
		// Retail's own captured epic re-entry to the map it had just left as normal minted a FRESH instance
		// (consecutive playfield tokens), which is exactly the stay-in rule refusing a difficulty mismatch.
		[Fact]
		public void EpicEntry_GetsItsOwnCopy_AndReentryRespectsTheDifficulty()
		{
			var world = new WorldState();

			// A Shared map: normal entry lands in the shared world, an epic one never does.
			Assert.Equal(WorldState.DefaultInstance, world.ResolveEntryInstance(null, 1004, MapInstanceMode.Shared));
			var epic = world.ResolveEntryInstance(null, 1004, MapInstanceMode.Shared, epic: true);
			Assert.NotEqual(WorldState.DefaultInstance, epic);

			// The player stands in their epic copy: an epic re-entry stays, a NORMAL one must not reuse it.
			var p = Player(1, 0, 0);
			world.Place(p, 1004, epic, p.Position, Quaternion.Identity);
			world.ZoneFor(1004, epic).IsEpic = true;
			Assert.Equal(epic, world.ResolveEntryInstance(p, 1004, MapInstanceMode.Shared, epic: true));
			Assert.NotEqual(epic, world.ResolveEntryInstance(p, 1004, MapInstanceMode.Shared));

			// A team's epic copy is bound separately from the same team's normal copy of the same map.
			var teamNormal = world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB);
			var teamEpic = world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB, true);
			Assert.NotEqual(teamNormal, teamEpic);
		}

		// #512: a GROUP map is one copy per TEAM. Retail's own group entry names the team in the PlayfieldProxy's
		// owner slots, so the routing has to be by team id and not by character.
		[Fact]
		public void Group_RoutesTheWholeTeamIntoOneCopy_AndADifferentTeamElsewhere()
		{
			var world = new WorldState();
			var first = world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB);
			Assert.NotEqual(WorldState.DefaultInstance, first); // a group map is always somebody's copy, never shared

			var leader = Player(1, 0, 0);
			world.Place(leader, 1092, first, leader.Position, Quaternion.Identity);

			// A member arriving later JOINS the team's copy rather than minting a second one...
			var member = Player(2, 1, 0);
			var second = world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB);
			Assert.Equal(first, second);
			world.Place(member, 1092, second, member.Position, Quaternion.Identity);
			Assert.Same(world.ZoneForEntity(leader), world.ZoneForEntity(member));

			// ...while another team gets its own, and a player with no team gets a private copy.
			Assert.NotEqual(first, world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x00500099));
			Assert.NotEqual(first, world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group));

			// The wire side: the zone knows it is the team's, which is what puts (0xDEA9, teamId) on the proxy.
			Assert.Equal(0x000F08DBu, world.TeamOwning(1092, first));
			Assert.Null(world.TeamOwning(1092, WorldState.DefaultInstance));
		}

		// The lifetime half: "empty = gone". Retail's leave message is "You will be unable to return to this
		// instance", and the part of that which follows from state we hold is that a copy nobody is in is not the
		// team's copy any more.
		[Fact]
		public void Group_EmptyCopyIsGone_AndReentryMintsANewOne()
		{
			var world = new WorldState();
			var instance = world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB);
			var player = Player(1, 0, 0);
			world.Place(player, 1092, instance, player.Position, Quaternion.Identity);

			// Still occupied: the same team comes back to the same copy.
			Assert.Equal(instance, world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB));

			world.Remove(player);
			Assert.NotEqual(instance, world.ResolveEntryInstance(null, 1092, MapInstanceMode.Group, 0x000F08DB));
		}

		[Fact]
		public void Shared_PlacesEveryoneInOneCopy( /* #268 */)
		{
			var world = new WorldState();
			var a = world.ResolveEntryInstance(null, 4010, MapInstanceMode.Shared);
			var b = world.ResolveEntryInstance(null, 4010, MapInstanceMode.Shared);
			Assert.Equal(WorldState.DefaultInstance, a);
			Assert.Equal(WorldState.DefaultInstance, b); // a Shared map always resolves to the one shared copy

			var p1 = Player(1, 0, 0);
			var p2 = Player(2, 1, 0);
			world.Place(p1, 4010, a, p1.Position, Quaternion.Identity);
			world.Place(p2, 4010, b, p2.Position, Quaternion.Identity);

			Assert.Same(world.ZoneForEntity(p1), world.ZoneForEntity(p2)); // same zone object
			Assert.Equal(2, world.ZoneForEntity(p1).Count);
			// and they see each other via interest
			var events = InterestManager.ResolveMove(world.ZoneForEntity(p1), p1, 50f);
			Assert.Contains(events, e => e.Subject.Handle == 2);
		}

		[Fact]
		public void Solo_GivesEachPlayerAPrivateCopy_NoCrossVisibility( /* #268 */)
		{
			var world = new WorldState();
			var i1 = world.ResolveEntryInstance(null, 4010, MapInstanceMode.Solo);
			var i2 = world.ResolveEntryInstance(null, 4010, MapInstanceMode.Solo);
			Assert.NotEqual(WorldState.DefaultInstance, i1);
			Assert.NotEqual(i1, i2); // a fresh private copy per entry

			var p1 = Player(1, 0, 0);
			var p2 = Player(2, 0, 0); // SAME position as p1
			world.Place(p1, 4010, i1, p1.Position, Quaternion.Identity);
			world.Place(p2, 4010, i2, p2.Position, Quaternion.Identity);

			Assert.NotSame(world.ZoneForEntity(p1), world.ZoneForEntity(p2));
			Assert.Equal(1, world.ZoneForEntity(p1).Count); // alone in their own copy
			// p1 does NOT see p2 despite the identical position - different instances.
			var events = InterestManager.ResolveMove(world.ZoneForEntity(p1), p1, 50f);
			Assert.DoesNotContain(events, e => e.Subject.Handle == 2);
		}

		[Fact]
		public void Tick_NpcEngagesAndDisengages_EmitsCombatModeEffects()
		{
			// A hostile NPC with a player in aggro range engages: the tick flips InCombat and emits NpcEnterCombat so the
			// broadcaster can draw the NPC's weapon (#218). When the player leaves range it disengages: InCombat clears
			// and NpcExitCombat is emitted (sheathe). The transition fires once, not every tick.
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, new Vector3(0, 0, 0), Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = true;
			var player = Player(1, 5, 0); // within aggro range (20)
			world.Place(npc, 2040, new Vector3(0, 0, 0), Quaternion.Identity);
			world.Place(player, 2040, player.Position, Quaternion.Identity);

			var first = world.Tick(0.1);
			Assert.Contains(first, e => e.Type == WorldTickEffectType.NpcEnterCombat && e.Entity == npc);
			Assert.True(npc.InCombat);

			// Already engaged: a second tick must NOT re-emit enter-combat.
			var second = world.Tick(0.1);
			Assert.DoesNotContain(second, e => e.Type == WorldTickEffectType.NpcEnterCombat);

			// Player walks far away -> NPC disengages and sheathes.
			player.Position = new Vector3(500, 0, 0);
			var third = world.Tick(0.1);
			Assert.Contains(third, e => e.Type == WorldTickEffectType.NpcExitCombat && e.Entity == npc);
			Assert.False(npc.InCombat);
		}

		// #620: the two sides of the shield rose are owned by different ends. A player enters the world already guarding
		// the retail default (one shield per arc) and the tick never touches it - it is the CLIENT that moves a player's
		// guard, and it uploads each change. An NPC is the mirror: no rose at all until the tick rolls one on engage.
		[Fact]
		public void Tick_RollsAnNpcsRose_AndNeverTouchesAPlayers()
		{
			var world = new WorldState();
			var npc = WorldEntity.ForNpc(10, 2040, new Vector3(0, 0, 0), Quaternion.Identity, "Mob", EntityVitals.FromMax(100, 0, 100));
			npc.Hostile = true;
			var player = Player(1, 5, 0); // within aggro range (20), so the NPC engages

			Assert.Equal(CombatShieldCodec.DefaultDistribution, player.ShieldDistribution);
			Assert.Equal(0u, npc.ShieldDistribution); // an unengaged NPC has no rose

			world.Place(npc, 2040, npc.Position, Quaternion.Identity);
			world.Place(player, 2040, player.Position, Quaternion.Identity);

			var effects = world.Tick(0.1);

			Assert.Contains(effects, e => e.Type == WorldTickEffectType.NpcShieldRose && e.Entity == npc);
			Assert.NotEqual(0u, npc.ShieldDistribution);
			// The player is skipped outright: no rose effect names them, and their guard is exactly where they left it.
			Assert.DoesNotContain(effects, e => e.Entity == player);
			Assert.Equal(CombatShieldCodec.DefaultDistribution, player.ShieldDistribution);

			// ...and it stays put over the shift cadence, which would have moved a guard on an NPC several times over.
			for (var i = 0; i < 200; i++)
				world.Tick(0.1);

			Assert.Equal(CombatShieldCodec.DefaultDistribution, player.ShieldDistribution);
		}

		[Fact]
		public void ZoneFor_MintsAMonotonicPlayfieldToken()
		{
			var world = new WorldState();

			var first = world.ZoneFor(2040).PlayfieldToken;
			var second = world.ZoneFor(4010).PlayfieldToken;

			Assert.NotEqual(0u, first); // every zone has a token from creation (despawns never skipped for lack of one)
			Assert.True(second > first, "playfield tokens are allocated monotonically");
			// re-fetching the same map returns the same zone, not a fresh token
			Assert.Equal(first, world.ZoneFor(2040).PlayfieldToken);
		}

		private static WorldEntity NpcTemplate(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 0, false, $"N{handle}", null!)
			{
				Position = new Vector3(x, 0, z)
			};
		}

		private static WorldEntity Player(uint handle, float x, float z)
		{
			return new WorldEntity(handle, 0, true, $"P{handle}", null!)
			{
				Position = new Vector3(x, 0, z)
			};
		}
	}
}

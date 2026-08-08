using System.Linq;
using System.Numerics;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs.Text;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// What makes an NPC say a bark (#694). Ours used to run every bark off one clock and then filter who HEARD it,
	// so range gated the audience but nothing gated the speech: the crier could deliver "You should consider joining
	// a guild, <name>!" to a player who had been standing there for ten minutes, or to nobody at all.
	//
	// The captured set says barks split three ways, and the row already carries the discriminator - the `channel`
	// component of the token. `NpcChatterBarks.TriggerOf` is that split; these tests pin it against the shipped rows
	// so a re-harvest that changes the corpus cannot quietly move a line onto the wrong cue.
	public class NpcBarkTriggerTests
	{
		[Fact]
		public void EveryShippedChannel_MapsToACue()
		{
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var byChannel = db.NpcChatter.AsNoTracking().Select(c => c.Channel).Distinct().OrderBy(c => c).ToArray();

			// The whole corpus: 6 channels over 25 rows / 8 NPC types. If a re-harvest adds a seventh, this fails and
			// the new one has to be classified deliberately rather than silently defaulting to Ambient.
			//
			// It has earned its keep TWICE. The first 2026-08-08 gameplay captures added `broadcastcycle` (the
			// Chronicler of Deeds' achievement announcements), which would have defaulted to Ambient - the right
			// answer there, since the name states the cue. The armoury captures the same day added `eventactions`
			// (the Cimmerian child's `CityLifeCimmerianChildReactToPlayer`), where the default would have been
			// WRONG: it reacts to a player and carries a `PC.name` slot, so it is Detection.
			Assert.Equal(new[] { "broadcastcycle", "conversation1", "conversation2", "event", "eventactions", "sendtell" }, byChannel);
		}

		[Theory]
		[InlineData("conversation1", BarkTrigger.Ambient)] // RenownBystanderLovers / RenownBystanderChild - overheard idle chatter
		[InlineData("conversation2", BarkTrigger.Ambient)] // RenownBystanderWomen / RenownBystanderLovers
		[InlineData("sendtell", BarkTrigger.Detection)] //    RenownCrier - a TELL, addressed at the player it detected
		[InlineData("event", BarkTrigger.EncounterScript)] // RnBLadyInDistressBrainScript - the #502 encounter's own trigger
		[InlineData("broadcastcycle", BarkTrigger.Ambient)] // AchievementsPetHeraldOfDeeds - a broadcast, on a cycle
		[InlineData("eventactions", BarkTrigger.Detection)] // CityLifeCimmerianChildReactToPlayer - reacts TO a player
		public void ChannelNamesTheCue(string channel, BarkTrigger expected)
		{
			Assert.Equal(expected, NpcChatterBarks.TriggerOf(Bark(channel)));
		}

		// An unknown channel falls to Ambient on purpose: a cosmetic line at the wrong moment beats a line wired to a
		// cue we have not decoded, which would never fire at all.
		[Fact]
		public void UnknownChannel_IsAmbient()
		{
			Assert.Equal(BarkTrigger.Ambient, NpcChatterBarks.TriggerOf(Bark("somethingnew")));
		}

		// The second evidence line for the same split, and it runs ONE WAY only: a bark carrying a session slot is
		// never ambient - it names a player, so something must have picked that player - but the converse is false.
		// The crier has TWO `sendtell` lines and only one of them (SayInChat 57, `DetectedChar.name`) names anybody;
		// SayInChat 10 is an unaddressed tell. A tell still has exactly one recipient, so it is still detection-
		// triggered, which is precisely why the CHANNEL is what the cue is read off and the slot is not a proxy for it.
		[Fact]
		public void NoAmbientBark_CarriesASessionSlot()
		{
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = db.NpcChatter.AsNoTracking().Include(c => c.Parameters).ToList();
			Assert.NotEmpty(rows);

			foreach (var row in rows)
			{
				if (NpcChatterBarks.TriggerOf(NpcChatterColumns.ToBark(row)) != BarkTrigger.Ambient)
					continue;

				Assert.DoesNotContain(row.Parameters, p => RemoteFormatSession.IsSessionBarkSlot(p.Name));
			}
		}

		// And the asymmetry itself, pinned: an addressed bark is never ambient, but an unaddressed one may still be a
		// tell. If a re-harvest ever puts a session slot on a `conversation*` line, the test above fails and this one
		// says what the rule was.
		[Fact]
		public void TheCrier_HasAnAddressedAndAnUnaddressedTell()
		{
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var tells = db.NpcChatter.AsNoTracking().Include(c => c.Parameters).Where(c => c.Channel == "sendtell").ToList();

			Assert.Equal(2, tells.Count);
			Assert.All(tells, t => Assert.Equal(BarkTrigger.Detection, NpcChatterBarks.TriggerOf(NpcChatterColumns.ToBark(t))));
			Assert.Single(tells, t => t.Parameters.Any(p => RemoteFormatSession.IsSessionBarkSlot(p.Name)));
		}

		[Fact]
		public void ANewcomerIsGreetedOnce_NotEveryTick()
		{
			var world = new WorldState();
			var crier = Crier(world);
			var player = Place(world, 2, true, 0, 5);

			Assert.Single(Greetings(world.Tick(0.1), crier, player));

			// Standing there is not arriving: the clock keeps running, the greeting does not.
			for (var i = 0; i < 50; i++)
				Assert.Empty(Greetings(world.Tick(0.1), crier, player));
		}

		[Fact]
		public void APlayerOutOfRange_IsNotGreeted()
		{
			var world = new WorldState();
			var crier = Crier(world);
			Place(world, 2, true, 0, WorldState.GreetingRange + 10f);

			Assert.DoesNotContain(world.Tick(0.1), e => e.Type == WorldTickEffectType.NpcGreeting);
		}

		// Walking away and coming back IS a fresh arrival - that is what a greeting means. The player has to leave the
		// wider forget band first, so loitering on the greeting boundary cannot re-trigger it.
		[Fact]
		public void LeavingAndReturning_GreetsAgain()
		{
			var world = new WorldState();
			var crier = Crier(world);
			var player = Place(world, 2, true, 0, 5);
			Assert.Single(Greetings(world.Tick(0.1), crier, player));

			// Out past the greeting range but still inside the forget band: still remembered, so no second greeting.
			player.Position = new Vector3(0, 0, WorldState.GreetingRange + 5f);
			Assert.Empty(Greetings(world.Tick(0.1), crier, player));
			player.Position = new Vector3(0, 0, 5);
			Assert.Empty(Greetings(world.Tick(0.1), crier, player));

			// Now past the forget band, and back.
			player.Position = new Vector3(0, 0, WorldState.GreetingForgetRange + 5f);
			world.Tick(0.1);
			player.Position = new Vector3(0, 0, 5);
			Assert.Single(Greetings(world.Tick(0.1), crier, player));
		}

		[Fact]
		public void EachPlayerIsGreetedSeparately()
		{
			var world = new WorldState();
			var crier = Crier(world);
			var first = Place(world, 2, true, 0, 5);
			Assert.Single(Greetings(world.Tick(0.1), crier, first));

			var second = Place(world, 3, true, 0, 6);
			var effects = world.Tick(0.1).Where(e => e.Type == WorldTickEffectType.NpcGreeting).ToList();
			Assert.Single(effects);
			Assert.Equal(second.Handle, effects[0].Target?.Handle);
		}

		// An NPC with no detection bark never emits one, however many players walk past - the pools are what decide,
		// not the tick.
		[Fact]
		public void AnAmbientOnlyNpc_NeverGreets()
		{
			var world = new WorldState();
			var mute = WorldEntity.ForNpc(1, 4010, Vector3.Zero, Quaternion.Identity, "Bystander", EntityVitals.FromMax(100, 0, 100));
			mute.ChatterLines = [Bark("conversation1")];
			world.Place(mute, 4010, Vector3.Zero, Quaternion.Identity);
			Place(world, 2, true, 0, 5);

			Assert.DoesNotContain(world.Tick(0.1), e => e.Type == WorldTickEffectType.NpcGreeting);
		}

		private static System.Collections.Generic.List<WorldTickEffect> Greetings(System.Collections.Generic.IReadOnlyList<WorldTickEffect> effects,
			WorldEntity crier, WorldEntity player)
		{
			return effects
				.Where(e => e.Type == WorldTickEffectType.NpcGreeting && e.Entity.Handle == crier.Handle && e.Target?.Handle == player.Handle)
				.ToList();
		}

		private static WorldEntity Crier(WorldState world)
		{
			var crier = WorldEntity.ForNpc(1, 4010, Vector3.Zero, Quaternion.Identity, "Tatianus the Crier", EntityVitals.FromMax(100, 0, 100));
			crier.GreetingLines = [Bark("sendtell")];
			world.Place(crier, 4010, Vector3.Zero, Quaternion.Identity);
			return crier;
		}

		private static WorldEntity Place(WorldState world, uint handle, bool isPlayer, float x, float z)
		{
			var entity = new WorldEntity(handle, 4010, isPlayer, "P", null!);
			world.Place(entity, 4010, new Vector3(x, 0, z), Quaternion.Identity);
			return entity;
		}

		private static NpcChatterBark Bark(string channel)
		{
			return new NpcChatterBark(1, channel, 1, "Group", 50015, "key", 0, []);
		}
	}
}

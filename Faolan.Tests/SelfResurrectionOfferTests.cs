using System.Numerics;
using System.Threading.Tasks;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// The self-resurrection OFFER's lifecycle (#890): armed while alive by the consumable, answered when dead.
	//
	// The wire half is gated in ResurrectRequestCodecTests against the one captured request. This is the half no
	// capture covers at all - the traced session used its potion and never died - so what is asserted here is the
	// rule the item's own tooltip states: resurrect "at the location of their death", restoring the spell's
	// percentage, an option that "will persist until it is used".
	public class SelfResurrectionOfferTests
	{
		private const uint MapId = 4010;
		private const uint Player = 0x01317CF8;
		private const uint PotionSpell = 4840951; // "Self Resurrection", what the Potion's own effect names
		private const uint Percent = 100;

		private static (WorldSimulation Sim, RecordingNetworkClient Client, WorldEntity Entity) Alive()
		{
			var sim = new WorldSimulation(null!, null!, NullLogger.Instance, null!);
			sim.BindOutbound(new RecordingWorldOutbound());
			var client = new RecordingNetworkClient(Player);
			var entity = new WorldEntity(Player, MapId, true, "NecroL80", client)
			{
				Vitals = new EntityVitals
				{
					MaxHealth = 1000,
					MaxStamina = 400,
					MaxMana = 600,
					Health = 1000,
					Stamina = 400,
					Mana = 600
				}
			};

			sim.Players[client] = entity;
			sim.World.Place(entity, MapId, new Vector3(100f, 0f, 100f), Quaternion.Identity);

			return (sim, client, entity);
		}

		private static void Kill(WorldEntity entity)
		{
			entity.Vitals!.Health = 0;
			entity.Vitals.Stamina = 0;
			entity.Vitals.Mana = 0;
		}

		[Fact]
		public async Task AnArmedOffer_RevivesTheDeadPlayerAtTheOffersPercentage()
		{
			var (sim, client, entity) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);
			Kill(entity);

			Assert.True(await sim.ResurrectFromOffer(client, PotionSpell));

			Assert.False(entity.Vitals!.IsDead);
			Assert.Equal(1000u, entity.Vitals.Health);
			Assert.Equal(400u, entity.Vitals.Stamina);
			Assert.Equal(600u, entity.Vitals.Mana); // all THREE pools, which is what the tooltip promises
		}

		// A Minor Elixir of Resurgence restores 25, an Exceptional 75 - the percentage is the SPELL's, so the tiers
		// differ by nothing but this number.
		[Theory]
		[InlineData(25u, 250u)]
		[InlineData(50u, 500u)]
		[InlineData(75u, 750u)]
		public async Task ThePercentageIsTheOffers(uint percent, uint expectedHealth)
		{
			var (sim, client, entity) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, percent);
			Kill(entity);

			Assert.True(await sim.ResurrectFromOffer(client, percent == 0 ? 0 : PotionSpell));
			Assert.Equal(expectedHealth, entity.Vitals!.Health);
		}

		// It revives IN PLACE. Resurrecting where you fell is the whole point of the item, and it is the one revive
		// that needs no destination - so the entity must not have moved.
		[Fact]
		public async Task ItRevivesWhereThePlayerFell()
		{
			var (sim, client, entity) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);
			Kill(entity);
			var deathPosition = entity.Position;

			Assert.True(await sim.ResurrectFromOffer(client, PotionSpell));

			Assert.Equal(deathPosition, entity.Position);
		}

		// "until it is used" - once, and then it is gone.
		[Fact]
		public async Task AnOfferIsConsumedByTheResurrection()
		{
			var (sim, client, entity) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);
			Kill(entity);

			Assert.True(await sim.ResurrectFromOffer(client, PotionSpell));
			Assert.False(sim.HasSelfResurrection(Player, PotionSpell));

			Kill(entity);
			Assert.False(await sim.ResurrectFromOffer(client, PotionSpell)); // a second death is not covered
		}

		// The spell id is the TOKEN we sent, echoed back: an answer naming a different one is not an answer to this
		// offer, and must not spend it.
		[Fact]
		public async Task AnAnswerNamingAnotherSpell_IsNotThisOffer()
		{
			var (sim, client, entity) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);
			Kill(entity);

			Assert.False(await sim.ResurrectFromOffer(client, 4332780));
			Assert.True(entity.Vitals!.IsDead);
			Assert.True(sim.HasSelfResurrection(Player, PotionSpell)); // still armed
		}

		// The offer only answers a DEATH. Taking it while alive would consume the item's whole point for nothing.
		[Fact]
		public async Task ALivePlayerCannotTakeTheOffer()
		{
			var (sim, client, _) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);

			Assert.False(await sim.ResurrectFromOffer(client, PotionSpell));
			Assert.True(sim.HasSelfResurrection(Player, PotionSpell));
		}

		[Fact]
		public async Task WithNoOffer_NothingHappens()
		{
			var (sim, client, entity) = Alive();
			Kill(entity);

			Assert.False(await sim.ResurrectFromOffer(client, PotionSpell));
			Assert.True(entity.Vitals!.IsDead);
		}

		// CancelResurrectionRequest drops it - and, like the accept, only the offer it actually names.
		[Fact]
		public void DecliningDropsTheOffer()
		{
			var (sim, _, _) = Alive();
			sim.ArmSelfResurrection(Player, PotionSpell, Percent);

			Assert.False(sim.CancelSelfResurrection(Player, 4332780)); // another offer's id
			Assert.True(sim.HasSelfResurrection(Player, PotionSpell));

			Assert.True(sim.CancelSelfResurrection(Player, PotionSpell));
			Assert.False(sim.HasSelfResurrection(Player, PotionSpell));
		}
	}
}

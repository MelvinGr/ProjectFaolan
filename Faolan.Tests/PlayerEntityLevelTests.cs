using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Faolan.Tests
{
	// A player's LIVE entity has to carry their level. It did not - `Level` was set for NPCs only, so every player
	// entity ran at Level 0, and two things read it without anyone noticing:
	//
	//   * BuffStatEffects resolves a RAMPED buff delta off it (#599 - the same buff is +887 health at level 20 and
	//     +17,500 at level 80), so every ramped buff on a player was resolved at level 0;
	//   * the attribute REBASE passes it to CharacterMaxStats.ClassBaseHundredths, which THROWS on 0.
	//
	// The throw is what surfaced it, and only in one narrow case: the rebase runs only when an equip CHANGES an
	// attribute, so equipping a piece with an attribute bonus threw out of the packet loop and dropped the player's
	// connection. Everything else read the level-0 answer quietly.
	public class PlayerEntityLevelTests
	{
		[Fact]
		public void RegisterPlayerInWorld_SeedsTheEntitysLevelFromTheCharacter()
		{
			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance, null!);
			var client = new RecordingNetworkClient(0x0802E5D4);
			client.Session.Character = new Character
			{
				Id = 0x0802E5D4,
				Name = "NecroL80",
				Class = ConanClass.Necromancer,
				Level = 80,
				MapId = 2000
			};

			sim.RegisterPlayerInWorld(client);

			Assert.Equal(80u, sim.PlayerWorldEntity(client)!.Level);
		}

		// The end-to-end shape of the crash: a level-80 player whose ATTRIBUTES change (what an equip does) must
		// recompute without throwing. At Level 0 this is an ArgumentOutOfRangeException out of ClassBaseHundredths.
		[Fact]
		public void RecomputingAfterAnAttributeChange_DoesNotThrow_ForARegisteredPlayer()
		{
			var sim = new WorldSimulation(null!, null!, null!, NullLogger.Instance, null!);
			var client = new RecordingNetworkClient(0x0802E5D4);
			client.Session.Character = new Character
			{
				Id = 0x0802E5D4,
				Name = "NecroL80",
				Class = ConanClass.Necromancer,
				Level = 80,
				MapId = 2000
			};

			sim.RegisterPlayerInWorld(client);
			var entity = sim.PlayerWorldEntity(client)!;

			// The equip's effect: new gear attributes, so the recompute takes the rebase branch.
			entity.GearBonuses = new System.Collections.Generic.Dictionary<CharacterStatId, int>
			{
				[CharacterStatId.Constitution] = 25,
				[CharacterStatId.Intelligence] = 40
			};

			// Recompute rather than Sync: Sync's only extra job is loading the catalog from the DB, and an EMPTY catalog
			// is the right input anyway - this is about the attribute rebase, not about any buff.
			BuffStatEffects.Recompute(entity, BuffStatEffects.BuildCatalog([]));

			Assert.Equal(80u, entity.Level);
			Assert.Equal(25u, entity.Attributes.Constitution); // the rebase actually ran
		}
	}
}

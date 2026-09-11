using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.GameServer.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// A player's live entity has to carry their level. With `Level` set for NPCs only, every player entity runs at
	// Level 0, and two things read it:
	//
	//   * BuffStatEffects resolves a ramped buff delta off it (the same buff is +887 health at level 20 and +17,500
	//     at level 80), so every ramped buff on a player resolves at level 0;
	//   * the attribute rebase passes it to CharacterMaxStats.ClassBaseHundredths, which throws on 0.
	//
	// The throw is the only visible symptom, and only in one narrow case: the rebase runs when an equip changes an
	// attribute, so equipping a piece with an attribute bonus throws out of the packet loop and drops the player's
	// connection. Everything else reads the level-0 answer quietly.
	public class PlayerEntityLevelTests
	{
		[Fact]
		public void RegisterPlayerInWorld_SeedsTheEntitysLevelFromTheCharacter()
		{
			var sim = TestWorld.Simulation();
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

		// The end-to-end shape of the crash: a level-80 player whose attributes change (what an equip does) must
		// recompute without throwing. At Level 0 this is an ArgumentOutOfRangeException out of ClassBaseHundredths.
		[Fact]
		public void RecomputingAfterAnAttributeChange_DoesNotThrow_ForARegisteredPlayer()
		{
			var sim = TestWorld.Simulation();
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

			// Recompute rather than Sync: Sync's only extra job is loading the catalog from the DB, and an empty catalog
			// is the right input anyway - this is about the attribute rebase, not about any buff.
			BuffStatEffects.Recompute(entity, BuffStatEffects.BuildCatalog([]));

			Assert.Equal(80u, entity.Level);
			Assert.Equal(25u, entity.Attributes.Constitution); // the rebase actually ran
		}
	}
}

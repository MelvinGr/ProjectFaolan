using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// "why is there no per-NPC movement speed to import?" answered against the corpus itself rather than against
	// the all-null columns it produced.
	//
	// `NpcStatCores.RunSpeed`, `.MaxRunSpeed` and `.OocMovementMultiplier` are null on all 1,018 shipped rows. That is
	// only evidence of a harvest gap if the mapping works, which is what
	// `NpcStatCoreColumnsTests.EveryStatCoreProtoField_SurvivesTheRoundTrip_EvenTheOnesNoCaptureSets` says. This gate closes
	// the loop from the other end: it walks every captured spawn in the corpus and asks the protobuf directly whether
	// retail ever set the fields (proto presence, not a zero value - see ShouldSerialize).
	//
	// If retail ever does send one, this fails and the movement-speed question reopens with real data behind it.
	public class NpcMovementSpeedFieldsTests
	{
		[Fact]
		public void NoCapturedNpcSpawn_CarriesAMovementSpeed()
		{
			var npcRecords = 0;
			var present = new List<string>();

			foreach (var iir in new[] { IirTypes.SimpleCharFullUpdateIIR, IirTypes.FullPlayerCharacterIIR })
			foreach (var (source, body) in CorpusExtract.OfType((uint)iir))
			{
				if (!SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				// `npc_stats` present is the record being a creature rather than a player (the same signal
				// CharacterStatsFlags_Bit28_IsTheNpcBit checks the flag against).
				if (read.Message.npc_stats == null)
					continue;

				// The three movement fields live on CharacterStats (31/32/33), not on npc_stats.
				var npc = read.Message.Stats;
				if (npc == null)
					continue;

				npcRecords++;

				// Proto presence, so an explicit 0 would still count as "retail sent it".
				if (npc.ShouldSerializeRunSpeed())
					present.Add($"run_speed={npc.RunSpeed} on {read.Message.Name} in {source}");
				if (npc.ShouldSerializeMaxRunSpeed())
					present.Add($"max_run_speed={npc.MaxRunSpeed} on {read.Message.Name} in {source}");
				if (npc.ShouldSerializeOocMovementMultiplier())
					present.Add($"ooc_movement_multiplier={npc.OocMovementMultiplier} on {read.Message.Name} in {source}");
			}

			// Without this the gate passes vacuously the day the corpus slice stops carrying creature spawns.
			Assert.True(npcRecords > 500, $"only {npcRecords} captured NPC spawns were read - the gate would be vacuous");
			Assert.True(present.Count == 0,
				$"retail DOES set a movement-speed field on an NPC spawn ({present.Count} of {npcRecords} records) - "
				+ $"#714 should import it instead of measuring:\n  " + string.Join("\n  ", present.GetRange(0, present.Count < 10 ? present.Count : 10)));
		}
	}
}

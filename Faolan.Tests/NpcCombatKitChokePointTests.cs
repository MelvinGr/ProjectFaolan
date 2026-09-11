using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// An NPC that can fight has three properties nothing else derives for it - whether it shoots
	// (`RangedAttacker`), how far it holds (`AttackRange`), and which combos it swings (`MeleeComboId` /
	// `ClassComboIds`). They come from the kit it is wearing, and the only place that knows the kit is the code that
	// mints the entity.
	//
	// So every path that mints one has to stamp them: `ZonePopulationLoader` (the placed spawns), `AmbientPoolService`
	// (the spawn-area pools) and `EncounterService` (the boss swap, its lure and the quest ambushers). A path that
	// does not leaves its mobs fighting unarmed with no combo, whatever they are holding.
	//
	// The failure mode is what makes this worth a gate rather than a comment: it is invisible. An ambient Poacher
	// looks right because every captured Poacher kit happens to be melee, so the beach crowd fights correctly by luck
	// rather than by construction, and the fault surfaces only when a pool first resolves to an archer template -
	// i.e. as a bug report about one zone, long after the cause.
	//
	// The census is deliberately coarse - per file, not per call - because what is worth catching is "a new path
	// started minting NPCs". That shows up as a new file here, and adding one means saying which kind it is.
	public class NpcCombatKitChokePointTests
	{
		// file -> why its `WorldEntity.ForNpc` calls do not need the combat kit.
		private static readonly Dictionary<string, string> Exempt = new()
		{
			["WorldEntity.cs"] = "the factory itself - it takes no equipment and has nothing to derive from",
		};

		[Fact]
		public void EveryPathThatMintsAnNpc_StampsItsCombatKit()
		{
			var offenders = new List<string>();
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan"), "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal)
				    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", System.StringComparison.Ordinal))
					continue;

				var name = Path.GetFileName(file);
				if (Exempt.ContainsKey(name))
					continue;

				var text = File.ReadAllText(file);
				if (!text.Contains("WorldEntity.ForNpc(", System.StringComparison.Ordinal))
					continue;

				// All four, because they fail independently: wiring the two weapon-reach ones into a path and leaving
				// the two combo ones behind is exactly the half-fix this gate exists to catch.
				var missing = new[] { "RangedAttacker", "AttackRange", "MeleeComboId", "ClassComboIds" }
					.Where(p => !text.Contains($".{p} =", System.StringComparison.Ordinal))
					.ToList();
				if (missing.Count > 0)
					offenders.Add($"{name} (no {string.Join(", ", missing)})");
			}

			Assert.True(offenders.Count == 0,
				"these files mint an NPC with `WorldEntity.ForNpc` and never stamp its combat kit, so whatever they spawn fights "
				+ "unarmed with no combos regardless of what it is holding (#837). Derive them from the kit at the mint site, or add "
				+ "the file to `Exempt` with the reason it has no kit. Offenders: " + string.Join("; ", offenders));
		}
	}
}

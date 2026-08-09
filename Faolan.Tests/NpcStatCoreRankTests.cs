using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// `NpcStatCore.NpcType` is the client's `npc_type` (NpcStats field 14) - the NPC kind that decides the nameplate
	// rank decoration (#508).
	//
	// ⚠️ The pin below is on TWO NPCs and that is ALL it establishes. The claim "3 is the skull rank" was retracted:
	// the same value covers `Tamed Puma` and `Pleasurable Company`, so it is broader than the skull. Keaira and King
	// Conan carrying 3 remains a real, annotation-backed fact worth gating - it just does not generalise to the
	// other 57. See NpcStatCore.NpcType and docs/protocol/gaps.md.
	//
	// It is already emitted by the live spawn path, so the only way this regresses is through the DATA: a NULL
	// silently drops the rank and the boss renders as an ordinary mob, with nothing anywhere to say so. That is the
	// failure this gates. The importer harvests the column from captured spawn bodies, so a re-harvest against a
	// narrower corpus is exactly how a row would lose it.
	public class NpcStatCoreRankTests
	{
		// The two NPCs the chat annotation compares - "talked to npc keira (skulls next to name. like king conan)".
		// They are the whole basis for connecting 3 to the skull at all, so if either stops being 3 the reading
		// itself is what broke, not just one row.
		private const uint KeairaTemplateId = 15090;
		private const uint KingConanTemplateId = 1917;

		[Fact]
		public async Task EveryNpc_CarriesARank()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var unranked = await db.NpcStatCores.Where(s => s.NpcType == null).CountAsync(ct);
			Assert.True(unranked == 0, $"{unranked} NPC stat core(s) have a NULL NpcType, so those NPCs render with no nameplate rank.");
		}

		[Fact]
		public async Task TheAnnotatedSkullBosses_AreRank3()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			foreach (var templateId in new[] { KeairaTemplateId, KingConanTemplateId })
			{
				var core = await db.NpcStatCores.FirstOrDefaultAsync(s => s.NpcTemplateId == templateId, ct);
				Assert.True(core != null, $"template {templateId} has no stat core - it is one of the two NPCs #508's identification rests on");
				Assert.Equal(3u, core!.NpcType);
			}
		}

		// The kind is a small closed enum. A fifth value appearing means a re-harvest found something the reading in
		// NpcStatCore.NpcType does not cover, and that comment (and the V0 in gaps.md) needs revisiting rather than
		// the new value being quietly absorbed.
		[Fact]
		public async Task NpcType_TakesOnlyTheFourObservedValues()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var values = await db.NpcStatCores.Where(s => s.NpcType != null).Select(s => s.NpcType!.Value)
				.Distinct().OrderBy(v => v).ToListAsync(ct);

			Assert.Equal([1u, 2u, 3u, 4u], values);
		}
	}
}

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
	// ⭐ The SKULL itself is `normalset` (stat 205), pinned from the client's own target-portrait updater - and the
	// same two NPCs fall in that set too, which is the gate below. Two independent routes, one answer.
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

		// ⭐ #758: the three values the client TITLES are 1 Minion / 2 Mini Boss / 3 Boss, and 4 is untitled. Gated by
		// MEMBERSHIP rather than by the count, because the count is what a re-harvest changes and the membership is
		// what the identification rests on: if King Conan ever stopped being 3, or a Rabbit stopped being 1, the
		// reading is what broke.
		[Theory]
		[InlineData(1917, 3u)] // King Conan       - Boss
		[InlineData(15090, 3u)] // Keaira          - Boss
		[InlineData(9643, 3u)] // Ape King         - Boss
		[InlineData(56, 2u)] // Taskmaster Olaf    - Mini Boss
		[InlineData(9763, 1u)] // Hen              - Minion
		[InlineData(8253, 1u)] // Rabbit           - Minion
		public async Task TheTitledNpcs_CarryTheirTier(uint templateId, uint expected)
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var core = await db.NpcStatCores.FirstOrDefaultAsync(s => s.NpcTemplateId == templateId, TestContext.Current.CancellationToken);

			Assert.NotNull(core);
			Assert.Equal(expected, core!.NpcType);
		}

		// ...and 4 - the value the client renders NO title for - has to stay the majority, because a boss title over
		// most of the world's NPCs is the visible failure this identification would cause if the mapping inverted.
		[Fact]
		public async Task TheUntitledValueIsTheMajority()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var total = await db.NpcStatCores.CountAsync(ct);
			var untitled = await db.NpcStatCores.CountAsync(s => s.NpcType == 4, ct);

			Assert.True(untitled * 2 > total, $"only {untitled} of {total} NPCs are npc_type 4 - the rest would render a Minion/Mini Boss/Boss title (#758)");
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

		// The skull set, straight from the client: FUN_0163eec0 reads GetStat(0xCD) and shows SkullLeft/SkullRight
		// for exactly these values. Gated on the shipped rows so a re-harvest that loses `normalset` - the column is
		// NULL for 972 of 1,194 templates already - cannot silently take the skulls off the two NPCs an annotation
		// says had them.
		[Fact]
		public async Task KeairaAndKingConan_FallInTheClientsSkullSet()
		{
			int[] skullValues = [8, 14, 15, 19, 20, 22, 23, 24, 26, 46, 47, 48, 49, 50, 51];

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.NpcStatCores.AsNoTracking()
				.Join(db.NpcTemplates, c => c.NpcTemplateId, t => t.TemplateId, (c, t) => new { t.Name, c.Normalset })
				.Where(x => x.Name == "Keaira" || x.Name == "King Conan")
				.ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(2, rows.Count);
			Assert.All(rows, r => Assert.True(r.Normalset != null && skullValues.Contains((int)r.Normalset),
				$"{r.Name} has normalset {r.Normalset?.ToString() ?? "NULL"}, which the client would draw WITHOUT skulls"));
		}
	}
}
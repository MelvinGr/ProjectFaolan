using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.GameServer.Combat;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// docs/subsystems/combos.md claims ComboActionParser is "validated against all 130 distinct Actions (109
	// directional, 21 non-directional, 0 unknown)". Nothing validated it: the only caller was ComboService, dropped
	// as dead in #353, and its tests went with it - leaving the parser unreferenced by anything, with a doc saying
	// otherwise. This is that claim, restored as a test against the shipped catalog.
	//
	// The Actions column is the RDB catalog's own text, and it is inconsistent on purpose - the same key appears as
	// "UR", "CombatUpperRight", "Up Right", "UpRight" and "1_Combatupperleft". That normalisation table IS the
	// harvested knowledge here, so the gate that matters is the LAST assertion: zero unknown tokens. A new catalog
	// spelling must fail loudly rather than be silently dropped from a combo's input sequence.
	public class ComboActionParserTests
	{
		[Fact]
		public async Task EveryCatalogActionsValue_ParsesOrIsKnownNonDirectional()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var actions = await db.Combos.Select(c => c.Actions).Distinct().ToListAsync(ct);
			Assert.True(actions.Count >= 130, $"only {actions.Count} distinct Actions values - the catalog looks unimported");

			// A value is accounted for when it parses into a directional sequence, or when it carries a recognised
			// non-directional marker, or when the catalog simply left it blank (160 of the 744 rows do - these are
			// the combos granted and displayed but not performed by a key sequence). Anything else is an unknown
			// spelling, which must fail rather than be silently dropped.
			var unknown = actions
				.Where(a => !string.IsNullOrWhiteSpace(a))
				.Where(a => !ComboActionParser.TryParse(a, out _) && !ComboActionParser.IsNonDirectional(a))
				.ToList();

			Assert.True(unknown.Count == 0, "unrecognised Actions token(s) - add the spelling to ComboActionParser.Map:\n  " + string.Join("\n  ", unknown));

			var directional = actions.Count(a => ComboActionParser.TryParse(a, out _));
			Assert.True(directional >= 100, $"only {directional} directional combos parsed - the normalisation table has regressed");
		}

		[Theory]
		// The five spellings of one key the catalog actually uses, plus the step-numbered form.
		[InlineData("UL", ComboInput.UpperLeft)]
		[InlineData("CombatUpperLeft", ComboInput.UpperLeft)]
		[InlineData("Up Left", ComboInput.UpperLeft)]
		[InlineData("UpLeft", ComboInput.UpperLeft)]
		[InlineData("1_Combatupperleft", ComboInput.UpperLeft)]
		public void EverySpellingOfAKey_NormalisesToTheSameInput(string token, ComboInput expected)
		{
			Assert.True(ComboActionParser.TryParse(token, out var sequence));
			Assert.Equal([expected], sequence);
		}

		[Fact]
		public void NonDirectionalMarkers_DoNotParseAsASequence()
		{
			// Spellweaving / reactive procs have no key sequence, so a caller matching directional input must not see
			// them as an empty-or-partial one.
			foreach (var marker in new[] { "action", "MagicCombo", "Magic_Reactive", "Melee_Reactive", "Combo_Reactive" })
			{
				Assert.False(ComboActionParser.TryParse(marker, out var sequence), marker);
				Assert.Empty(sequence);
			}
		}

		// #455: the direction after a marker is a SELECTOR, not a step. The catalog settles it - the marked rows form
		// a complete 4 (family) x 5 (direction) grid with every cell present EXACTLY once, and the three reactive
		// families share one GameActionId per direction. A key SEQUENCE could not come out that way.
		[Fact]
		public async Task MarkedCombos_AreAFourByFiveGrid_TheDirectionSelectingTheProc()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var rows = await db.Combos.Where(c => c.Actions != null && c.Actions != "").ToListAsync(ct);
			var marked = rows
				.Select(c => (c.Name, c.GameActionId, Parsed: ComboActionParser.TryParseTriggered(c.Actions!, out var t, out var d), Trigger: t, Direction: d))
				.Where(x => x.Parsed)
				.ToList();

			// Four families x five directions, one row each.
			Assert.Equal(20, marked.Count);
			Assert.Equal(4, marked.Select(x => x.Trigger).Distinct().Count());
			Assert.Equal(5, marked.Select(x => x.Direction).Distinct().Count());
			Assert.All(marked.GroupBy(x => (x.Trigger, x.Direction)), cell => Assert.Single(cell));

			// `action` is the fifth marker and the one with no direction after it, so it is a known marker that does
			// NOT reach the grid.
			Assert.True(ComboActionParser.TryParseMarker("action", out var placeholder));
			Assert.Equal(ComboTrigger.Placeholder, placeholder);
			Assert.False(ComboActionParser.TryParseTriggered("action", out _, out _));

			// The three REACTIVE families are the same five effects - the family says what triggers the proc, the
			// direction says which proc. MagicCombo is the caster's own distinct set.
			var reactive = marked.Where(x => x.Trigger != ComboTrigger.MagicCombo).ToList();
			Assert.Equal(15, reactive.Count);
			Assert.All(reactive.GroupBy(x => x.Direction), byDirection => Assert.Single(byDirection.Select(x => x.GameActionId).Distinct()));
			Assert.Empty(reactive.Select(x => x.GameActionId).Intersect(marked.Where(x => x.Trigger == ComboTrigger.MagicCombo).Select(x => x.GameActionId)));

			// Each marked row's own Name is its effect's name, which is what makes the column mapping self-checking:
			// `Clumsy_combo` / `Clumsy_magic` / `Clumsy` all carry Clumsy's id.
			var byId = await db.Spells.Where(s => marked.Select(m => m.GameActionId).Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
			Assert.Equal(10, byId.Count); // 5 reactive effects + 5 MagicCombo effects
		}

		[Theory]
		[InlineData("Combo_Reactive,CombatHead", ComboTrigger.ComboReactive, ComboInput.Head)]
		[InlineData("Magic_Reactive,Combatupperleft", ComboTrigger.MagicReactive, ComboInput.UpperLeft)]
		[InlineData("Melee_Reactive,Combatlowerright", ComboTrigger.MeleeReactive, ComboInput.LowerRight)]
		[InlineData("MagicCombo,Combatlowerleft", ComboTrigger.MagicCombo, ComboInput.LowerLeft)]
		public void TryParseTriggered_KeepsTheDirectionTheSequenceParserDiscards(string actions, ComboTrigger trigger, ComboInput direction)
		{
			// TryParse still refuses these - they are not a key sequence - but the direction is no longer lost.
			Assert.False(ComboActionParser.TryParse(actions, out _));
			Assert.True(ComboActionParser.TryParseTriggered(actions, out var parsedTrigger, out var parsedDirection));
			Assert.Equal(trigger, parsedTrigger);
			Assert.Equal(direction, parsedDirection);
		}

		[Fact]
		public void AnUnknownToken_IsRejectedRatherThanDropped()
		{
			// The whole sequence fails: a combo whose second key we cannot name would otherwise silently become a
			// one-key combo and match the wrong input.
			Assert.False(ComboActionParser.TryParse("UL,NotAKey", out var sequence));
			Assert.Empty(sequence);
		}
	}
}

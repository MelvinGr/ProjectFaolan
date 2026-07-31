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

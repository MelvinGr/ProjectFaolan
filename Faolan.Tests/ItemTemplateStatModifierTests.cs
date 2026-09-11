using System;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// Golden decode of the item stat-modifier region (docs/rdb/item-template-stats.md, #3). Ground truth is the
	// in-client-verified Royal Signet tooltip (template 3774542, verified 2026-06-07 in docs/re/spawn-dissection.md
	// step (b)): +30 Constitution / +52 Magic Damage / +15 Hit Rating. The bytes below are that item's real decrypted
	// RDB template body (the span after the 12-byte RDB-Id header), so this is not self-referential - it proves the
	// parser reproduces an independently-observed tooltip.
	public class ItemTemplateStatModifierTests
	{
		private const string RoyalSignetBodyBase64 = "RXUAAAQAAAAPAAAAFwAAAEwvAABMAAAAAAAAAB4AAAAFAAAAAAAAAAEAAAUqAQAAAAYAAFgAAAAJAAAA8wEAAAMAAADwAQAArA0AAGgB" +
		                                             "AABkAAAASgAAAORxAAA2AAAASAAAAAwAAAAGkjEAFQAAACEAAAAMAAAAUm95YWwgU2lnbmV0AgAAAA4AAADEDwAANc8AAAAAAAAEAAAA" +
		                                             "AAAAAAEAAAAAAAAAAgAAAAkAAAA2AQAADwAAADXPAAAAAAAABAAAAAAAAAABAAAAAAAAAAIAAAAJAAAAJgMAACwBAAA1zwAAAAAAAAQA" +
		                                             "AAAAAAAAAQAAAAAAAAACAAAACQAAAF0DAABQFAAAFgAAACQAAADiBwAABgAAAOIHAAAAAAAApB8AAF0AAAA=";

		// The region is not 4-byte aligned to the body in most templates, and a word-aligned scan cannot see an
		// unaligned record at all - it silently returns zero, which is why 2873 of 3248 templates read as "no stats" while
		// the client applied a full list from the same bytes. This builds the exact 10-word record the parser anchors on,
		// at every possible byte phase, and requires it to be found in all four.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void FindsARecordAtEveryByteAlignment(int phase)
		{
			const uint kind = 53045, statId = (uint)CharacterStatId.Constitution, amount = 580;
			uint[] record = [0, kind, 0, 4, 0, 1, 0, 2, 9, statId, amount];

			var body = new byte[phase + record.Length * 4 + 8];
			for (var i = 0; i < record.Length; i++)
				body.WriteU32Le(phase + i * 4, record[i]);

			var mods = ItemTemplateStatModifiers.Parse(body);

			var mod = Assert.Single(mods);
			Assert.Equal(statId, mod.StatId);
			Assert.Equal(amount, mod.WireAmount);
			Assert.Equal(kind, mod.Kind);
		}

		[Fact]
		public void IgnoresGarbageAndResourceRefSignatureHits()
		{
			// A statId outside the real range (an RDB resource type id / garbage) must be dropped even if the 6-word
			// signature matches. Records: [marker 0][4][0][1][0][2][9][statId][amount].
			uint[] words =
			[
				0xCF35, 0, 4, 0, 1, 0, 2, 9, 0x0001, 6600, // valid: +66 HealthMax
				0xCF35, 0, 4, 0, 1, 0, 2, 9, 1000017, 42 // resource-ref statId -> dropped
			];
			var body = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				BitConverter.GetBytes(words[i]).CopyTo(body, i * 4);

			var mods = ItemTemplateStatModifiers.Parse(body);

			Assert.Single(mods);
			Assert.Equal((uint)CharacterStatId.HealthMax, mods[0].StatId);
			Assert.Equal(6600u, mods[0].WireAmount);
		}

		[Fact]
		public void RoyalSignet_DecodesToItsInClientTooltip()
		{
			var body = Convert.FromBase64String(RoyalSignetBodyBase64);

			var mods = ItemTemplateStatModifiers.Parse(body);

			// Exactly the three tooltip lines, in file order (Hit Rating, Constitution, Magic Damage).
			Assert.Equal(3, mods.Count);

			Assert.Equal(0x136u, mods[0].StatId); // Hit Rating
			Assert.Equal(15u, mods[0].WireAmount);

			Assert.Equal((uint)CharacterStatId.Constitution, mods[1].StatId);
			Assert.Equal(300u, mods[1].WireAmount);

			Assert.Equal(0x35du, mods[2].StatId); // Magic Damage
			Assert.Equal(5200u, mods[2].WireAmount);

			Assert.Equal(15, Display(0x136)); // +15 Hit Rating (×1)
			Assert.Equal(30, Display((uint)CharacterStatId.Constitution)); // +30 Constitution (×10)
			Assert.Equal(52, Display(0x35d)); // +52 Magic Damage (×100)
			return;

			// ...and each divided by its wire scale reproduces the displayed tooltip value.
			int Display(uint statId)
			{
				return (int)(mods.First(m => m.StatId == statId).WireAmount / ((CharacterStatId)statId).GetScale());
			}
		}

		// `Parse` is the positional pair view - payload words 0 and 1 - and that is (stat, value) only for the
		// kinds whose key list puts them there. `ParseStatModifiers` addresses by key and drops the kinds that are not
		// a modifier of the carrier's own stats at all. The payloads below are real feat records, from
		// `--report feat-effect-kinds`.
		[Fact]
		public void ParseStatModifiers_ReadsTheStatByKeyAndDropsWhatIsNotOne()
		{
			uint[] words =
			[
				// `Modify` (53045) - [Stat, Value]. The one kind the pair view was right about.
				53045, 0, 4, 0, 1, 0, 2, 9, 280, 2000,
				// `DisplayLearnAbility` (53176) - [Line, 187]. No stat slot at all: the pair view read the Line word
				// (1) as CharacterStatId.HealthMax and the ability ref as a +4232751 health bonus.
				53176, 0, 4, 0, 1, 0, 2, 9, 1, 4232751,
				// `SpellStatModifier` (53054) - [Spell, Stat, Value, Amount]. It has a stat, but the stat belongs to
				// the spell, not the carrier - and the pair view filed the spell id as the stat id. Its own table is
				// FeatSpellStatModifiers.
				53054, 0, 4, 0, 1, 0, 2, 9, 3962388, 347, 200, 0,
				// `AddPercentSkillModifier` (53189) - [66, SkillType, 67, Value]. No stat slot; the pair view reported
				// (66's value, a skill type).
				53189, 0, 4, 0, 1, 0, 2, 9, 1095, 2, 1095, 5,
				// `InterpolatedModify` (53220) - [Stat, low, lowLevel, high, highLevel]. A stat modifier, but its
				// magnitude is key 174 and not key 175 `Amount`; the pair view landed on 174 by luck of position.
				53220, 0, 4, 0, 1, 0, 2, 9, 1, 887, 20, 17500, 80
			];
			var body = new byte[4 + words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				body.WriteU32Le(4 + i * 4, words[i]);

			var mods = ItemTemplateStatModifiers.ParseStatModifiers(body);

			Assert.Equal([53045u, 53220u], mods.Select(m => m.Kind));
			Assert.Equal([280u, 1u], mods.Select(m => m.StatId));
			Assert.Equal([2000u, 887u], mods.Select(m => m.WireAmount));

			// …and the interpolated one carries the whole ramp beside its low endpoint, while the plain
			// `Modify` carries none - `Ramp` is what tells the two apart without a kind allowlist at the call site.
			Assert.Null(mods[0].Ramp);
			Assert.Equal(new LevelRamp(887, 17500, 20, 80), mods[1].Ramp);

			// ...where the positional view keeps four of the five, two of them nonsense - including the +4232751
			// health. The one it does drop, `SpellStatModifier`, it drops only because that spell id happens to
			// exceed the stat-id bound: a smaller one would have sailed through as a stat.
			var positional = ItemTemplateStatModifiers.Parse(body);
			Assert.Equal(4, positional.Count);
			Assert.Contains(positional, m => m is { Kind: 53176, StatId: 1, WireAmount: 4232751 });
		}

		// The two damage families state a range, and `InterpolatedHit` states it twice - once at each end of a
		// level ramp. Reading only the first magnitude reported a 58.40-71.40 hit as a flat 58.40 and a level-80
		// number as a level-75 one, on 2,900 shipped rows. The payloads are `Gangrenous Stench`'s tick (221068) and
		// `Lifestrike (Rank 1)`'s damage half, both read off the client's own tooltip.
		[Fact]
		public void ParseSpellStatEffects_CarriesBothEndpointsAndBothRamps()
		{
			uint[] words =
			[
				// `Hit` (53002) - [Stat, min, max, 71]. The upper endpoint is key 37 and was being dropped.
				53002, 0, 4, 0, 1, 0, 2, 9, 27, unchecked((uint)-410), unchecked((uint)-500), 3,
				// `InterpolatedHit` (53219) - [Stat, 168, 170, 172, 169, 171, 173, 71]: the min ramps 168 -> 169 and
				// the max 170 -> 171, both over levels 172..173. `IsLevelRamp` tests 174/175 - the interpolated
				// modify family's slots - so this read as having no ramp at all.
				53219, 0, 4, 0, 1, 0, 2, 9, 27, unchecked((uint)-5840), unchecked((uint)-7140), 75, unchecked((uint)-6220), unchecked((uint)-7610), 80, 3
			];
			var body = new byte[4 + words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				body.WriteU32Le(4 + i * 4, words[i]);

			var mods = ItemTemplateStatModifiers.ParseSpellStatEffects(body);

			Assert.Equal([53002u, 53219u], mods.Select(m => m.Kind));

			// The flat hit: both endpoints, no ramp.
			Assert.Equal(-410, (int)mods[0].WireAmount);
			Assert.Equal(-500, mods[0].MaxAmount);
			Assert.Null(mods[0].Ramp);
			Assert.Null(mods[0].MaxRamp);

			// The interpolated one: 58.40-71.40 at level 75 ramping to 62.20-76.10 at 80, which is four magnitudes
			// and two levels. `Ramp` is the low endpoint's and `MaxRamp` the high one's - they share their levels.
			Assert.Equal(-5840, (int)mods[1].WireAmount);
			Assert.Equal(-7140, mods[1].MaxAmount);
			Assert.Equal(new LevelRamp(-5840, -6220, 75, 80), mods[1].Ramp);
			Assert.Equal(new LevelRamp(-7140, -7610, 75, 80), mods[1].MaxRamp);
		}
	}
}

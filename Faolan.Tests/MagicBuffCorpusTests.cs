using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #617: the WHOLE-CORPUS gate on the MagicBuff apply payload - 2,929 captured applies instead of the four
	// hand-picked `InlineData` rows in `MagicBuffDataCodecTests`, which were one capture's spellweave trance and its
	// components. Every captured apply is an instance of the same oracle (*this spell id -> the 54 bytes retail
	// actually sent*), including spells the capturing player never cast, because other players' and NPCs' casts are
	// broadcast to them too.
	//
	// ⚠️ IT DOES NOT ASSERT A BYTE-EXACT REBUILD, AND THAT IS THE POINT OF WRITING IT. The first version did, and it
	// failed immediately: three of the "constants" `MagicBuffDatas.Apply` writes are generalisations of those four
	// rows and are wrong across the corpus (see WhatTheBuilderGetsWrong below, and Gitea #658 for the decode). The
	// gate is therefore split into what we can honestly claim today:
	//
	//   * the fields that ARE invariant over all 2,929 applies - asserted at 100%;
	//   * the fields we source from a `Spells` row - asserted against it;
	//   * the KNOWN DIVERGENCES - asserted as measured counts, so the numbers cannot drift silently in either
	//     direction. When the decode lands, these turn into equalities and the counts go away.
	//
	// A "known divergence" pinned to a number is not a weakened test: it fails if the builder gets worse, if it gets
	// better without the gate being updated, and if the corpus changes shape underneath it.
	public class MagicBuffCorpusTests
	{
		private const int ApplyPayloadLength = 54;

		// The payload's field offsets, as MagicBuffData lays them out: Counter, CounterType, Duration, two bools, the
		// seven descriptor words, CastingLevel, ExpiryStamp.
		private const int CounterOffset = 0;
		private const int CounterTypeOffset = 4;
		private const int DurationOffset = 8;
		private const int Bool1Offset = 12;
		private const int Bool2Offset = 13;
		private const int Descriptor0Offset = 14;
		private const int Descriptor1Offset = 18;
		private const int SpellIdOffset = 22; // descriptor[2]
		private const int LevelOffset = 26; // descriptor[3]
		private const int Descriptor4Offset = 30;
		private const int Descriptor5Offset = 34;
		private const int Descriptor6Offset = 38;
		private const int CastingLevelOffset = 42;

		// The APPLY form's low byte. 1 is the END twin (no payload at all), 2 an UPDATE.
		private const byte ApplyForm = 0;

		// The shape claim everything else rests on, measured rather than assumed: an apply carries exactly 54 payload
		// bytes and an end carries none. A third length would mean a form we do not model.
		[Fact]
		public void EveryCapturedBody_IsEitherA54ByteApplyOrAPayloadlessEnd()
		{
			var odd = ParsedBodies()
				.Where(b => b.Buff.Data.Length is not (0 or ApplyPayloadLength))
				.Select(b => $"{b.Source}: form={b.Buff.Form} payload={b.Buff.Data.Length} bytes")
				.ToList();

			Assert.True(odd.Count == 0, "MagicBuff bodies with an unmodelled payload length:\n  " + string.Join("\n  ", odd.Take(10)));
		}

		// The words the builder writes as zero, and that retail agrees are zero on every single captured apply. These
		// are the parts of the payload our construction is genuinely verified on.
		[Theory, InlineData(CounterOffset, "Counter"), InlineData(CounterTypeOffset, "CounterType"), InlineData(Descriptor5Offset, "descriptor[5]"),
		 InlineData(Descriptor6Offset, "descriptor[6]")]
		public void TheZeroWords_AreZeroInEveryCapturedApply(int offset, string name)
		{
			var nonZero = CapturedApplies().Where(a => a.Buff.Data.ReadU32Be(offset) != 0).ToList();
			Assert.True(nonZero.Count == 0, $"{name} @{offset} is non-zero in {nonZero.Count} of {CapturedApplies().Count} applies, e.g. {nonZero.FirstOrDefault().Source}");
		}

		[Fact]
		public void Bool1_IsZeroInEveryCapturedApply()
		{
			Assert.Equal(0, CapturedApplies().Count(a => a.Buff.Data[Bool1Offset] != 0));
		}

		// The DURATION at +8, against the `Spells` row it is built from - the original point of this ticket.
		//
		// ⭐ It exposed a mechanic rather than a bug. 242 of the 2,929 applies carry a duration the row disagrees
		// with, and they are not noise: nearly all are LOWER than the row and the value DECREASES across a capture
		// for the same spell (0x00119110, a 4-hour buff: 1,434,700 -> 1,252,990 -> ... -> 1,180,241 cs against a row
		// of 1,440,000). That is retail re-sending a long buff with its REMAINING time, not its base duration - the
		// restore path #601 is about, here with 242 samples instead of an argument.
		//
		// So the gate asserts what it can: an apply whose duration EXCEEDS the row's is unexplained by that reading
		// and is asserted at ZERO (#659 landed the four rows it found); the shortfalls are pinned as a population
		// and characterised.
		[Fact]
		public void TheDuration_MatchesTheSpellRow_OrIsARestoreCarryingTheTimeLeft()
		{
			var applies = CapturedApplies();
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ids = applies.Select(a => a.Buff.SpellBaseId).Distinct().ToList();
			var durations = db.Spells.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id, s => s.DurationMs);

			var compared = 0;
			var exact = 0;
			var shortfall = new List<(uint Spell, uint Captured, uint Row)>();
			var over = new List<(uint Spell, string Line)>();
			foreach (var (source, buff) in applies)
			{
				if (!durations.TryGetValue(buff.SpellBaseId, out var durationMs))
					continue; // no row to construct from - not a builder failure

				compared++;
				var captured = buff.Data.ReadU32Be(DurationOffset);
				var expected = durationMs / 10;
				if (captured == expected)
					exact++;
				else if (captured < expected)
					shortfall.Add((buff.SpellBaseId, captured, expected));
				else
					over.Add((buff.SpellBaseId, $"{source} spell 0x{buff.SpellBaseId:X8}: captured {captured} cs > row {expected} cs"));
			}

			Assert.True(compared > 500, $"only {compared} applies resolved to a Spells row - the gate would be near-vacuous");
			Assert.Equal(2696, exact);

			// The shortfall population, pinned - and split, because it is not homogeneous: 192 are on buffs of an
			// hour or more (the restore reading above), and 41 are on shorter ones, which that reading does not
			// explain and which are part of #658's decode.
			Assert.Equal(233, shortfall.Count);
			Assert.Equal(41, shortfall.Count(s => s.Row < 360000));

			// ⭐ The other direction IS a data bug, and the gate found it: four spells whose `Spells.DurationMs` was 0
			// or 1 ms while retail sends a real duration - 0x003B3C9C and 0x004902C0 at 40 s, 0x00485DE6 at 15 s,
			// 0x004D3063 at 4 h. #659 sourced all four: the first three from the wire itself, where the same value
			// repeats across distinct captures (`CaptureSpellImporter.ImportBuffDurationsAsync`), and `XP Boost (4h)`
			// from its own RDB record, which is in the ability/effect catalog rather than the spell catalog
			// (`RdbSpellImporter.FillDurationsFromEffectCatalog`) - so its 1,268,055 cs apply moved to the shortfall
			// list, correctly, as the restore it always was.
			//
			// Asserted at ZERO rather than removed: an apply longer than the row it is built from can only be a wrong
			// row, so any new one is a finding.
			Assert.True(over.Count == 0, $"{over.Count} applies whose duration EXCEEDS the Spells row - a wrong Spells.DurationMs:\n  "
			                             + string.Join("\n  ", over.Select(o => o.Line).Take(10)));
		}

		// descriptor[2] is the spell's own base id, echoed inside the payload. 10 of the 2,929 disagree - pinned
		// rather than excused, because a drift either way is a finding.
		[Fact]
		public void DescriptorTwo_IsTheSpellsOwnBaseId()
		{
			var applies = CapturedApplies();
			var disagreeing = applies.Count(a => a.Buff.Data.ReadU32Be(SpellIdOffset) != a.Buff.SpellBaseId);
			Assert.Equal(10, disagreeing);
		}

		// ⚠️ The three places `MagicBuffDatas.Apply` is provably wrong, each pinned to its measured count so it cannot
		// drift unnoticed. Decode tracked as Gitea #658.
		//
		//   * descriptor[0] - built as the constant 1, captioned "the only descriptor word that is a non-zero constant
		//     across every captured apply". It is 1 on 2,606, 2 on 321 and 3 on 2, and it varies WITHIN a spell on only
		//     7 of the 256 spells seen - so it is close to a per-spell property, not a constant.
		//   * bool2 @13 - built as the constant 1. It is 1 on 1,806 and 0 on 1,123, varying within a spell on 3 of 256.
		//   * the two LEVEL slots - the builder fills descriptor[3] and CastingLevel from one `casterLevel` argument.
		//     Retail disagrees between them on 728 of the 2,929, so they are two different numbers.
		[Fact]
		public void WhatTheBuilderGetsWrong_IsExactlyThisMuch()
		{
			var applies = CapturedApplies();
			Assert.Equal(2929, applies.Count);

			var descriptor0 = applies.GroupBy(a => a.Buff.Data.ReadU32Be(Descriptor0Offset)).ToDictionary(g => g.Key, g => g.Count());
			Assert.Equal(new Dictionary<uint, int> { [1] = 2606, [2] = 321, [3] = 2 }, descriptor0);

			var bool2 = applies.GroupBy(a => a.Buff.Data[Bool2Offset]).ToDictionary(g => g.Key, g => g.Count());
			Assert.Equal(new Dictionary<byte, int> { [1] = 1806, [0] = 1123 }, bool2);

			Assert.Equal(728, applies.Count(a => a.Buff.Data.ReadU32Be(LevelOffset) != a.Buff.Data.ReadU32Be(CastingLevelOffset)));

			// descriptor[1] and [4] are near-zero rather than zero, so they are not in the verified set above.
			Assert.Equal(2, applies.Count(a => a.Buff.Data.ReadU32Be(Descriptor1Offset) != 0));
			Assert.Equal(134, applies.Count(a => a.Buff.Data.ReadU32Be(Descriptor4Offset) != 0));
		}

		// ⭐ descriptor[0] is an ARITY tag, not a flag (#658). The two applies where it is `3` are EXACTLY the two
		// where descriptor[1] is non-zero - 2 of 2 in both directions - and both carry 3165331, which is a
		// `Combos.Id`: `combo_npc_guardian_shield_70_decisive_opening`, on the buff `Decisive Opening`. So the word
		// says how the rest of the descriptor is populated, and its `3` form names the COMBO that applied the buff.
		//
		// Asserted as an equivalence rather than as two counts, because the equivalence is the finding: a builder
		// that writes a constant there is not merely writing a wrong number, it is writing the wrong SHAPE.
		[Fact]
		public void DescriptorZeroIsThree_ExactlyWhenDescriptorOneNamesACombo()
		{
			var applies = CapturedApplies();
			var arityThree = applies.Where(a => a.Buff.Data.ReadU32Be(Descriptor0Offset) == 3).ToList();
			var withSlotOne = applies.Where(a => a.Buff.Data.ReadU32Be(Descriptor1Offset) != 0).ToList();

			Assert.NotEmpty(arityThree);
			Assert.Equal(arityThree.Count, withSlotOne.Count);
			Assert.All(arityThree, a => Assert.NotEqual(0u, a.Buff.Data.ReadU32Be(Descriptor1Offset)));

			// The id really is in the combo catalog, which is what makes it a decode rather than a correlation.
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var referenced = withSlotOne.Select(a => a.Buff.Data.ReadU32Be(Descriptor1Offset)).Distinct().ToList();
			Assert.All(referenced, id => Assert.True(db.Combos.Any(c => c.Id == id), $"descriptor[1] = {id} is not a Combos.Id"));
		}

		// descriptor[4] is a BOOLEAN in a u32 slot (#658): its only non-zero value is 1, and it is clear on every
		// self-apply. The ten spells that carry it are hostile debuffs and DoTs, so it reads as a hostile marker on
		// a cast at someone else - but 554 of the 688 non-self applies leave it clear, so "not self" is a necessary
		// condition and not the whole rule. Pinned at both ends: the value set, and the self-apply exclusion.
		[Fact]
		public void DescriptorFour_IsAFlagAndNeverSetOnASelfApply()
		{
			var applies = CapturedApplies();
			var set = applies.Where(a => a.Buff.Data.ReadU32Be(Descriptor4Offset) != 0).ToList();

			Assert.Equal(134, set.Count);
			Assert.Equal([1u], set.Select(a => a.Buff.Data.ReadU32Be(Descriptor4Offset)).Distinct().ToList());
			Assert.DoesNotContain(set, a => a.Buff.ApplyFrom == a.Buff.ApplyTo);
		}

		// Both are nearly a property of the SPELL rather than of the cast, which is the shape that says the decode
		// should look at the spell's own record. Pinned so that reading stays true.
		//
		// ⚠️ That reading has since been TESTED and did not pay out: `--report buff-descriptor --report-arg rdb`
		// sweeps every word and every bit of the spell's 1040005 record - head, property run and the common prefix -
		// and nothing coarse beats the baseline. See docs/subsystems/npc-spell-applyspell.md.
		[Fact]
		public void DescriptorZeroAndBool2_AreNearlyPerSpell()
		{
			var perSpell = CapturedApplies().GroupBy(a => a.Buff.SpellBaseId).ToList();
			Assert.Equal(256, perSpell.Count);
			Assert.Equal(7, perSpell.Count(g => g.Select(a => a.Buff.Data.ReadU32Be(Descriptor0Offset)).Distinct().Count() > 1));
			Assert.Equal(3, perSpell.Count(g => g.Select(a => a.Buff.Data[Bool2Offset]).Distinct().Count() > 1));
		}

		// WHICH level each of the two slots holds (#658). The ticket had them as "two slots we fill from one
		// argument [that] are demonstrably two numbers", with the route "a cross-tab of each against the caster's
		// known level would separate them". Resolving handles to levels is not needed - SCOPE separates them, and
		// the answer is that one is the CASTER's level and the other the TARGET's:
		//
		//                            varies within CASTER      varies within TARGET
		//   descriptor[3] @26          20 of 1075 (1.9%)        48 of 1183 (4.1%)     -> the CASTER's
		//   CastingLevel  @42          36 of 1075 (3.3%)         6 of 1183 (0.5%)     -> the TARGET's
		//
		// `CastingLevel` is six times more stable grouped by target than by caster, and `descriptor[3]` is the other
		// way round. (Entities are keyed by (capture, handle): a handle is session-scoped, so grouping on the handle
		// alone merges unrelated entities across captures.)
		[Fact]
		public void TheTwoLevelSlots_AreTheCastersAndTheTargets()
		{
			var applies = CapturedApplies();
			var perCaster = applies.GroupBy(a => (Capture(a.Source), a.Buff.ApplyFrom)).ToList();
			var perTarget = applies.GroupBy(a => (Capture(a.Source), a.Buff.ApplyTo)).ToList();

			static int Unstable<T>(List<IGrouping<T, (string Source, MagicBuffIIR Buff)>> groups, int offset)
			{
				return groups.Count(g => g.Select(a => a.Buff.Data.ReadU32Be(offset)).Distinct().Count() > 1);
			}

			// The load-bearing comparison is each slot against ITSELF under the two groupings, not the two slots
			// against each other - the populations differ (1075 casters vs 1183 targets).
			Assert.True(Unstable(perCaster, LevelOffset) < Unstable(perTarget, LevelOffset),
				"descriptor[3] should be more stable per CASTER than per target");
			Assert.True(Unstable(perTarget, CastingLevelOffset) < Unstable(perCaster, CastingLevelOffset),
				"CastingLevel should be more stable per TARGET than per caster");

			// ...and by a wide margin on the one that matters: 6 against 36.
			Assert.True(Unstable(perCaster, CastingLevelOffset) >= 5 * Unstable(perTarget, CastingLevelOffset),
				"CastingLevel's target-stability should dominate, not merely edge out, its caster-stability");
		}

		// The corroboration, and the reason the two agree so often: 2,241 of the 2,929 applies are SELF-applies,
		// where the caster IS the target and the two levels are one number by construction. Split on that and the
		// agreement rate splits with it - which is what a caster/target pair predicts and a caster/spell pair
		// would not.
		//
		// ⚠️ 281 self-applies still disagree, so this is not the whole story. 238 of those 281 are ONE spell
		// (3911037) and the rest spread thinly over 17 more, so it reads as a per-spell exception rather than a
		// refutation - but it is why the field is not renamed yet.
		[Fact]
		public void TheyAgreeOnSelfApplies_AndLargelyDisagreeOtherwise()
		{
			var applies = CapturedApplies();
			var self = applies.Where(a => a.Buff.ApplyFrom == a.Buff.ApplyTo).ToList();
			var other = applies.Where(a => a.Buff.ApplyFrom != a.Buff.ApplyTo).ToList();

			static double AgreementRate(List<(string Source, MagicBuffIIR Buff)> rows)
			{
				return rows.Count == 0 ? 0 : (double)rows.Count(a => a.Buff.Data.ReadU32Be(LevelOffset) == a.Buff.Data.ReadU32Be(CastingLevelOffset)) / rows.Count;
			}

			Assert.True(self.Count > other.Count, "most captured applies are self-applies");
			Assert.True(AgreementRate(self) > 0.8, $"self-applies should nearly always agree, got {AgreementRate(self):P0}");
			Assert.True(AgreementRate(other) < 0.5, $"non-self applies should mostly disagree, got {AgreementRate(other):P0}");
		}

		// #677: the STREAM-IN re-assert. Retail tells a newly-arrived observer about an entity's already-running buff
		// with an ordinary apply carrying a NULL caster and the REMAINING time - and the corpus holds exactly one,
		// which is what `WorldBroadcaster.SendActiveBuffsOnStreamIn` is built from.
		//
		// It is one sighting, so what earns it a golden is that it is unique in three independent ways at once: it is
		// the only casterless apply in all 2,929, its `descriptor[3]` is 0 where every caster-bearing apply carries a
		// level, and its duration is 6.8% of the row's. Asserted as a byte-exact rebuild from `ApplyNoCaster`, so the
		// builder cannot drift from the one record that defines the shape.
		[Fact]
		public void TheCasterlessStreamInApply_RebuildsByteExact()
		{
			// ⚠️ NOT `is null or 0`. A null ApplyFrom is the class-0 "no caster reference at all" form (11 in the
			// corpus - the Racer buff and friends); this is the other one, a CHARACTER reference whose handle is
			// zero. Same 54-byte data region, different carrier, and only this one appears at a spawn.
			var one = Assert.Single(CapturedApplies(), a => a.Buff.ApplyFrom == 0);

			var payload = MagicBuffDataCodec.Parse(one.Buff.Data);
			Assert.Equal(0u, payload.Descriptor[3]); // no caster, so no caster level...
			Assert.Equal(80u, payload.CastingLevel); // ...while the TARGET's level still rides in CastingLevel

			var rebuilt = MagicBuffDataCodec.Build(MagicBuffDatas.ApplyNoCaster(one.Buff.SpellBaseId, payload.Duration, payload.ExpiryStamp, payload.CastingLevel));
			Assert.Equal(one.Buff.Data, rebuilt);

			// And the whole carrier, which is what pins the zero-handle caster reference rather than the class-0 one.
			Assert.Equal(MagicBuffCodec.Build(new MagicBuffIIR(one.Buff.ApplyTo, one.Buff.Env, one.Buff.SpellBaseId, one.Buff.Form, 0, rebuilt)),
				MagicBuffCodec.Build(one.Buff));
		}

		private static string Capture(string source)
		{
			return source[..source.IndexOf('#')];
		}

		private static List<(string Source, MagicBuffIIR Buff)> CapturedApplies()
		{
			return ParsedBodies().Where(b => b.Buff is { Form: ApplyForm, Data.Length: ApplyPayloadLength }).ToList();
		}

		private static List<(string Source, MagicBuffIIR Buff)> ParsedBodies()
		{
			// The corpus body carries its leading u32 length, exactly as BodyHex did.
			return CorpusExtract.OfType((uint)IirTypes.MagicBuffIIR)
				.Select(e => (e.Source, MagicBuffCodec.Parse(e.Body.AsSpan(sizeof(uint)))))
				.ToList();
		}
	}
}

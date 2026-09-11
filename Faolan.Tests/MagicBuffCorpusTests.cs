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
	// The whole-corpus gate on the MagicBuff apply payload - 2,929 captured applies rather than the four hand-picked
	// `InlineData` rows in `MagicBuffDataCodecTests`, which are one capture's spellweave trance and its components.
	// Every captured apply is an instance of the same oracle (*this spell id -> the 54 bytes retail actually sent*),
	// including spells the capturing player never cast, because other players' and NPCs' casts are broadcast to them
	// too.
	//
	// It does not assert a byte-exact rebuild, deliberately: three of the "constants" `MagicBuffDatas.Apply` writes
	// are generalisations of those four rows and are wrong across the corpus (see WhatTheBuilderGetsWrong below).
	// The gate is therefore split into what can honestly be claimed today:
	//
	//   * the fields that are invariant over all 2,929 applies - asserted at 100%;
	//   * the fields sourced from a `Spells` row - asserted against it;
	//   * the known divergences - asserted as measured counts, so the numbers cannot drift silently in either
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

		// Above every session-local entity handle in the corpus and below every player's character id - see
		// Bool2_IsWhetherTheCasterIsAPlayer.
		private const uint PlayerHandleFloor = 0x0100_0000;
		private const int Descriptor0Offset = 14;
		private const int Descriptor1Offset = 18;
		private const int SpellIdOffset = 22; // descriptor[2]
		private const int LevelOffset = 26; // descriptor[3]
		private const int Descriptor4Offset = 30;
		private const int Descriptor5Offset = 34;
		private const int Descriptor6Offset = 38;
		private const int CastingLevelOffset = 42;

		// The apply form's low byte. 1 is the end twin (no payload at all), 2 an update.
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

		// Retail sends an ApplySpell only for a spell that has something to apply. Sending one for a zero-duration
		// spell is not merely redundant: it aborts the client's `CoreMagicHolder::ActivateBuff` ("remaining time 0",
		// logged at error), and the spell's own GameActions - which for `Chill` is exactly one element,
		// `SetSpellLineRecastTime(41028, 400)` - never execute, so no cooldown starts.
		//
		// The nukes cast constantly through the corpus (Chill, Ice Strike, Lifestrike, Pestilential Blast) never
		// carry one. The handful that do resolve to a zero-duration row are pinned as a population rather than
		// asserted away, because they are our data gap, not retail's behaviour: they are real buffs
		// (`Armor of Faith`, `Emerald Aptitude`, `Resting`) whose `DurationMs` we have not imported. If that number
		// grows, a genuine zero-duration apply has appeared and the rule above needs re-testing; if it shrinks, the
		// missing durations have been filled and the count should come down with it.
		[Fact]
		public void EveryCapturedApply_CarriesASpellThatHasADuration()
		{
			var applies = CapturedApplies();
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ids = applies.Select(a => a.Buff.SpellBaseId).Distinct().ToList();
			var rows = db.Spells.Where(s => ids.Contains(s.Id)).ToDictionary(s => s.Id, s => new { s.Name, s.DurationMs });

			var resolved = 0;
			var zero = new List<string>();
			foreach (var (_, buff) in applies)
			{
				if (!rows.TryGetValue(buff.SpellBaseId, out var row))
					continue; // no row to judge against - a catalog gap, not a counter-example

				resolved++;
				if (row.DurationMs == 0)
					zero.Add($"{buff.SpellBaseId} {row.Name}");
			}

			Assert.True(resolved > 2000, $"expected the corpus's applies to resolve against Spells; only {resolved} did");

			// 7 of 5,423 s2c bodies, over three spells. A hard ceiling rather than an equality, so filling one of those
			// durations does not fail the gate it was meant to satisfy.
			Assert.True(zero.Count <= 7,
				$"{zero.Count} captured applies carry a spell with DurationMs == 0 (expected at most the 7 known missing durations): "
				+ string.Join(", ", zero.Distinct().Take(12)));
		}

		// The duration comparison lives in `--report buff-duration-check`, not here: it compares importer output
		// (`Spells.DurationMs`) against importer input (the captured applies), and against the frozen slice both sides
		// move whenever captures are added. Its content - "an apply cannot outlast the row it is built from" - is
		// asserted there, against the real dumps, where the two sides are always in step.

		// descriptor[2] is the spell's own base id, echoed inside the payload. 10 of the 2,929 disagree - pinned
		// rather than excused, because a drift either way is a finding.
		[Fact]
		public void DescriptorTwo_IsTheSpellsOwnBaseId()
		{
			var applies = CapturedApplies();
			var disagreeing = applies.Count(a => a.Buff.Data.ReadU32Be(SpellIdOffset) != a.Buff.SpellBaseId);
			Assert.Equal(10, disagreeing);
		}

		// The three places `MagicBuffDatas.Apply` is provably wrong, each pinned to its measured count so it cannot
		// drift unnoticed. Open work tracked in Gitea #658.
		//
		//   * descriptor[0] - built as the constant 1, captioned "the only descriptor word that is a non-zero constant
		//     across every captured apply". It is 1 on 2,606, 2 on 321 and 3 on 2, and it varies within a spell on only
		//     7 of the 256 spells seen - so it is close to a per-spell property, not a constant.
		//   * bool2 @13 - built as the constant 1. It is 1 on 1,806 and 0 on 1,123, varying within a spell on 3 of 256.
		//   * the two level slots - the builder fills descriptor[3] and CastingLevel from one `casterLevel` argument.
		//     Retail disagrees between them on 728 of the 2,929, so they are two different numbers.
		[Fact]
		public void WhatTheBuilderGetsWrong_IsExactlyThisMuch()
		{
			var applies = CapturedApplies();
			Assert.Equal(2929, applies.Count);

			var descriptor0 = applies.GroupBy(a => a.Buff.Data.ReadU32Be(Descriptor0Offset)).ToDictionary(g => g.Key, g => g.Count());
			Assert.Equal(new Dictionary<uint, int> { [1] = 2606, [2] = 321, [3] = 2 }, descriptor0);


			Assert.Equal(728, applies.Count(a => a.Buff.Data.ReadU32Be(LevelOffset) != a.Buff.Data.ReadU32Be(CastingLevelOffset)));

			// descriptor[1] and [4] are near-zero rather than zero, so they are not in the verified set above.
			Assert.Equal(2, applies.Count(a => a.Buff.Data.ReadU32Be(Descriptor1Offset) != 0));
			Assert.Equal(134, applies.Count(a => a.Buff.Data.ReadU32Be(Descriptor4Offset) != 0));
		}

		// `bool2` is whether the caster is a player, not a constant - a constant 1 is only true of the four hand-picked
		// rows the builder was generalised from. It splits the whole corpus with no exceptions, and it is a property of
		// the caster rather than of the spell. That scope statement needs the whole dumps rather than this slice, so it
		// is measured there instead of gated here: of the 159 casters that cast more than one distinct spell, zero vary
		// `bool2`, while 4 of the 128 spells cast by more than one caster do (`--report codec-sweep`'s corpus, 3,424
		// applies).
		//
		// A no-caster apply is 0, which is the same rule rather than a third case - and is what `ApplyNoCaster`
		// already writes, from the other direction.
		[Fact]
		public void Bool2_IsWhetherTheCasterIsAPlayer()
		{
			var applies = CapturedApplies();

			// A player's handle is their character id, so it sits far above the session-local handles the world hands
			// out to creatures. The threshold is the reading here, not the finding - what makes it a decode is that a
			// single cut separates the whole population with nothing either side of it.
			var wrong = applies.Where(a => (a.Buff.Data[Bool2Offset] != 0) != a.Buff.ApplyFrom is > PlayerHandleFloor).ToList();

			Assert.True(wrong.Count == 0, $"{wrong.Count} of {applies.Count} applies disagree, e.g. {wrong.FirstOrDefault().Source}");
			Assert.Contains(applies, a => a.Buff.Data[Bool2Offset] == 1); // both arms are populated, so the rule is not vacuous
			Assert.Contains(applies, a => a.Buff.Data[Bool2Offset] == 0);
		}

		// descriptor[0] is an arity tag, not a flag. The two applies where it is `3` are exactly the two where
		// descriptor[1] is non-zero - 2 of 2 in both directions - and both carry 3165331, which is a `Combos.Id`:
		// `combo_npc_guardian_shield_70_decisive_opening`, on the buff `Decisive Opening`. So the word says how the
		// rest of the descriptor is populated, and its `3` form names the combo that applied the buff.
		//
		// Asserted as an equivalence rather than as two counts, because the equivalence is the finding: a builder
		// that writes a constant there is not merely writing a wrong number, it is writing the wrong shape.
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

		// descriptor[4] is a boolean in a u32 slot: its only non-zero value is 1, and it is clear on every
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

		// Both are nearly a property of the spell rather than of the cast, which is the shape that says the decode
		// should look at the spell's own record. Pinned so that reading stays true.
		//
		// That reading has been tested and did not pay out: `--report buff-descriptor --report-arg rdb` sweeps every
		// word and every bit of the spell's 1040005 record - head, property run and the common prefix - and nothing
		// coarse beats the baseline. See docs/subsystems/npc-spell-applyspell.md.
		[Fact]
		public void DescriptorZeroAndBool2_AreNearlyPerSpell()
		{
			var perSpell = CapturedApplies().GroupBy(a => a.Buff.SpellBaseId).ToList();
			Assert.Equal(256, perSpell.Count);
			Assert.Equal(7, perSpell.Count(g => g.Select(a => a.Buff.Data.ReadU32Be(Descriptor0Offset)).Distinct().Count() > 1));
			Assert.Equal(3, perSpell.Count(g => g.Select(a => a.Buff.Data[Bool2Offset]).Distinct().Count() > 1));
		}

		// Which level each of the two slots holds. Scope separates them: one is the caster's level and the other the
		// target's.
		//
		//                            varies within caster      varies within target
		//   descriptor[3] @26          20 of 1075 (1.9%)        48 of 1183 (4.1%)     -> the caster's
		//   CastingLevel  @42          36 of 1075 (3.3%)         6 of 1183 (0.5%)     -> the target's
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

			// The load-bearing comparison is each slot against itself under the two groupings, not the two slots
			// against each other - the populations differ (1075 casters vs 1183 targets).
			Assert.True(Unstable(perCaster, LevelOffset) < Unstable(perTarget, LevelOffset),
				"descriptor[3] should be more stable per CASTER than per target");
			Assert.True(Unstable(perTarget, CastingLevelOffset) < Unstable(perCaster, CastingLevelOffset),
				"CastingLevel should be more stable per TARGET than per caster");

			// ...and by a wide margin on the one that matters: 6 against 36.
			Assert.True(Unstable(perCaster, CastingLevelOffset) >= 5 * Unstable(perTarget, CastingLevelOffset),
				"CastingLevel's target-stability should dominate, not merely edge out, its caster-stability");
		}

		// The corroboration, and the reason the two agree so often: 2,241 of the 2,929 applies are self-applies,
		// where the caster is the target and the two levels are one number by construction. Split on that and the
		// agreement rate splits with it - which is what a caster/target pair predicts and a caster/spell pair
		// would not.
		//
		// 281 self-applies still disagree, so this is not the whole story. 238 of those 281 are one spell (3911037)
		// and the rest spread thinly over 17 more, so it reads as a per-spell exception rather than a refutation - and
		// it is why the field is not renamed.
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

		// The stream-in re-assert. Retail tells a newly-arrived observer about an entity's already-running buff with an
		// ordinary apply carrying a null caster and the remaining time - and the corpus holds exactly one, which is
		// what `WorldBroadcaster.SendActiveBuffsOnStreamIn` is built from.
		//
		// It is one sighting, so what earns it a golden is that it is unique in three independent ways at once: it is
		// the only casterless apply in all 2,929, its `descriptor[3]` is 0 where every caster-bearing apply carries a
		// level, and its duration is 6.8% of the row's. Asserted as a byte-exact rebuild from `ApplyNoCaster`, so the
		// builder cannot drift from the one record that defines the shape.
		[Fact]
		public void TheCasterlessStreamInApply_RebuildsByteExact()
		{
			// Not `is null or 0`. A null ApplyFrom is the class-0 "no caster reference at all" form (11 in the
			// corpus - the Racer buff and friends); this is the other one, a character reference whose handle is
			// zero. Same 54-byte data region, different carrier, and only this one appears at a spawn.
			var one = Assert.Single(CapturedApplies(), a => a.Buff.ApplyFrom == 0);

			var payload = MagicBuffDataCodec.Parse(one.Buff.Data);
			Assert.Equal(0u, payload.Descriptor[3]); // no caster, so no caster level...
			Assert.Equal(80u, payload.CastingLevel); // ...while the target's level still rides in CastingLevel

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
			// The corpus body carries its leading u32 length.
			return CorpusExtract.OfType((uint)IirTypes.MagicBuffIIR)
				.Select(e => (e.Source, MagicBuffCodec.Parse(e.Body.AsSpan(sizeof(uint)))))
				.ToList();
		}
	}
}

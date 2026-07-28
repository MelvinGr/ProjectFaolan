using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// The acceptance test #447 asks for: a modelled stat's value REACHES THE WIRE, whichever baseline the character
	// landed on. The bug behind that issue was silent - editing `(CharacterStatId.GmLevel, 0x5)` in the static
	// StatList2Half changed nothing for a real login, because an imported character's sheet comes from its capture
	// and the static list only ever fed the fresh path.
	public class SnapshotStatSheetAuthoringTests
	{
		// Stats the CORPUS shows varying between captured characters, which we nonetheless carry at a fixed value.
		// Each needs a reason, and the reason may not be "we did not get to it" without an issue behind it - a stat
		// that varies in retail and is constant in our emit is a character wearing someone else's number.
		private static readonly Dictionary<CharacterStatId, string> NotModelledYet = new()
		{
			// The three captured PlayerIds belong to three captured characters, so the slot IS per-character - but
			// what it maps to (account? character? a retail-side id?) is not established, and our own character ids
			// are not it. Carried at the captured value rather than guessed at. Gitea #478.
			[CharacterStatId.PlayerId] = "per-character, mapping unknown (#478)",

			// Progression/statistics we do not track yet: play time, money, the PvP and kill/death counters, the
			// skill ratings. All read as character state we simply have no source for, not as protocol unknowns.
			// NB the list holds only what actually VARIES: MurderPoints, MinigamesAbandoned, SiegeWins/Losses and
			// Fatalities are 0 on every captured character, so carrying them as 0 is a fact, not a shortcut - and
			// the anti-rot half of the gate below rejects them if listed.
			[CharacterStatId.Played] = "play time not tracked",
			[CharacterStatId.Money] = "the wallet lives in Character.Money; snapshot slot not wired",
			[CharacterStatId.Deaths] = "kill/death counters not tracked",
			[CharacterStatId.Kills] = "kill/death counters not tracked",
			[CharacterStatId.LastKillNumber] = "kill/death counters not tracked",
			[CharacterStatId.PreviousHealth] = "derived from combat history we do not keep",
			[CharacterStatId.DeathEffectXp] = "death-penalty XP not tracked",
			[CharacterStatId.PvpExp] = "PvP progression not tracked",
			[CharacterStatId.PvpLevel] = "PvP progression not tracked",
			[CharacterStatId.PvpMaxExp] = "PvP progression not tracked",
			[CharacterStatId.FeatResets] = "feat-reset counter not tracked",
			[CharacterStatId.XpKillRange] = "XP-sharing range not modelled",
			[CharacterStatId.Timestamp] = "a capture-time stamp, not character state",
			[CharacterStatId.CaptureSkullWins] = "PvP minigame counters not tracked",
			[CharacterStatId.CaptureSkullLosses] = "PvP minigame counters not tracked",
			[CharacterStatId.AnnihilationWins] = "PvP minigame counters not tracked",
			[CharacterStatId.AnnihilationLosses] = "PvP minigame counters not tracked",

			// Skills and ratings: real character stats, but ours are not derived yet (#401 armour/mitigation is the
			// nearest live work).
			[CharacterStatId.MagicalResist] = "ratings not derived (#401)",
			[CharacterStatId.MagicRating] = "ratings not derived (#401)",
			[CharacterStatId.AttackRating] = "ratings not derived (#401)",
			[CharacterStatId.DefenseRating] = "ratings not derived (#401)",
			[CharacterStatId.Perception] = "skills not derived",
			[CharacterStatId.Climbing] = "skills not derived",
			[CharacterStatId.Hiding] = "skills not derived",
			[CharacterStatId.Skulk] = "skills not derived",
			[CharacterStatId.RunSpeed] = "movement stats not derived",

			// Named-unknown slots that vary: undecoded, so there is nothing to derive them FROM yet (#464).
			[CharacterStatId.Unknown15] = "undecoded slot (#464)",
			[CharacterStatId.Unknown32] = "undecoded slot (#464)",
			[CharacterStatId.Unknown46] = "undecoded slot (#464)",
			[CharacterStatId.ElderFrostSpecialization] = "undecoded slot (#464)"
		};

		// Stats authored on the BLOCK rather than in the sheet: SubClassPostAccessBlock.ApplyClassVitals writes these
		// from the character's class curve after the sheet is built.
		private static readonly CharacterStatId[] AuthoredByVitals =
		[
			CharacterStatId.HealthCurrent, CharacterStatId.HealthMax, CharacterStatId.ManaCurrent,
			CharacterStatId.ManaMax, CharacterStatId.StaminaCurrent, CharacterStatId.StaminaMax
		];

		// THE ACCEPTANCE TEST. Two different characters must produce two different sheets, in every slot the snapshot
		// claims to derive - and identically so whether the baseline was the fresh skeleton or a captured sheet.
		[Fact]
		public void AModelledStat_ReachesTheSheet_FromEitherBaseline()
		{
			var captured = SubClassStatBlock.FreshSheet()
				.Select(p => ((CharacterStatId)p.Id, p.Value == 0 ? 0xABCDu : p.Value)) // a "captured" baseline that agrees with the fresh one nowhere
				.ToList();

			foreach (var baseline in new IReadOnlyList<(CharacterStatId Id, uint Value)>?[] { null, captured })
			{
				var a = SnapshotStatSheet.For(Character(level: 12, exp: 345, Sex.Male), 0x1111, baseline);
				var b = SnapshotStatSheet.For(Character(level: 40, exp: 999, Sex.Female), 0x2222, baseline);

				Assert.Equal(12u, Value(a, CharacterStatId.Level));
				Assert.Equal(40u, Value(b, CharacterStatId.Level));
				Assert.Equal(345u, Value(a, CharacterStatId.Exp));
				Assert.Equal(999u, Value(b, CharacterStatId.Exp));
				Assert.Equal((uint)Sex.Male, Value(a, CharacterStatId.Sex));
				Assert.Equal((uint)Sex.Female, Value(b, CharacterStatId.Sex));
				Assert.Equal(0x1111u, Value(a, CharacterStatId.PlayfieldToken));
				Assert.Equal(0x2222u, Value(b, CharacterStatId.PlayfieldToken));
			}
		}

		// A derived stat is written at EVERY occurrence: a stat can appear in both lists and twice within StatList#2,
		// and a half-written sheet is what makes the client's view internally inconsistent.
		[Fact]
		public void ADerivedStat_IsWrittenAtEveryOccurrence()
		{
			var sheet = SnapshotStatSheet.For(Character(level: 33, exp: 7, Sex.Male), null, null);
			var levels = sheet.Where(p => p.Id == (uint)CharacterStatId.Level).ToList();

			Assert.True(levels.Count > 1, "Level appears once - this test cannot see a half-written sheet");
			Assert.All(levels, p => Assert.Equal(33u, p.Value));
		}

		// ⚠️ THE GATE. Every stat the CORPUS shows varying between captured characters must be authored from the
		// character, or listed above with a reason. Without this, a stat that varies in retail can sit in the static
		// baseline at one captured character's value and be shipped to everyone - which is exactly #447's complaint,
		// and is invisible until someone reads the sheet in-client.
		[Fact]
		public void EveryStatThatVariesInTheCorpus_IsAuthoredOrExplicitlyNotModelled()
		{
			var varying = VaryingStatsInCorpus();
			Assert.True(varying.Count > 30, $"only {varying.Count} varying stats - the corpus slice is wrong");

			var authored = new HashSet<uint>(SnapshotStatSheet.Authored.Select(id => (uint)id));
			foreach (var id in AuthoredByVitals)
				authored.Add((uint)id);

			// The declaration may not drift from the behaviour: anything two different characters disagree about has
			// to be declared. (The reverse is not checkable - ExpCurrentLevelBase is authored to 0 for everyone.)
			var a = SnapshotStatSheet.For(Character(level: 12, exp: 345, Sex.Male), 0x1111, null);
			var b = SnapshotStatSheet.For(Character(level: 40, exp: 999, Sex.Female), 0x2222, null);
			var undeclared = a.Where((p, i) => p.Value != b[i].Value).Select(p => p.Id).Where(id => !authored.Contains(id)).Distinct().ToList();
			Assert.True(undeclared.Count == 0, "authored but not declared in SnapshotStatSheet.Authored: " + string.Join(", ", undeclared));

			var unexplained = varying
				.Where(id => !authored.Contains(id))
				.Where(id => !NotModelledYet.ContainsKey((CharacterStatId)id))
				.Select(id => Enum.IsDefined(typeof(CharacterStatId), id) ? ((CharacterStatId)id).ToString() : $"0x{id:X}")
				.ToList();

			Assert.True(unexplained.Count == 0,
				$"{unexplained.Count} stat(s) vary between captured characters but are neither authored nor listed as not-modelled:\n  "
				+ string.Join("\n  ", unexplained));

			// And the allowlist may not rot: an entry that no longer varies is a claim about retail that the corpus
			// stopped supporting.
			var stale = NotModelledYet.Keys.Where(id => !varying.Contains((uint)id)).Select(id => id.ToString()).ToList();
			Assert.True(stale.Count == 0, "not-modelled entries that do not vary in the corpus (drop them):\n  " + string.Join("\n  ", stale));
		}

		// The distinct stat ids whose VALUE differs between captured characters.
		private static HashSet<uint> VaryingStatsInCorpus()
		{
			var byStat = new Dictionary<uint, HashSet<uint>>();

			foreach (var (_, sub, _) in CompletedQuestJournalTests.SubClassesForProbe())
			{
				var block = sub.PostAccessBlock;
				if (block == null)
					continue;

				foreach (var (start, words) in block.StatTables)
				{
					for (var i = start; i + 1 < start + words && i + 1 < block.Values.Count; i += 2)
					{
						byStat.TryAdd(block.Values[i], []);
						byStat[block.Values[i]].Add(block.Values[i + 1]);
					}
				}
			}

			return [.. byStat.Where(k => k.Value.Count > 1).Select(k => k.Key)];
		}

		private static Character Character(ushort level, uint exp, Sex sex)
		{
			return new Character
			{
				Id = 0x0200000B,
				Level = level,
				Experience = exp,
				Sex = sex,
				Class = ConanClass.Barbarian
			};
		}

		private static uint Value(List<(uint Id, uint Value)> sheet, CharacterStatId id)
		{
			return sheet.First(p => p.Id == (uint)id).Value;
		}
	}
}

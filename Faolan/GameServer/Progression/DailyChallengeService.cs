using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Progression
{
	// DAILY CHALLENGES (#509). Retail's whole model for this rides twelve ordinary character STATS, and every part of
	// it below is read off the client or measured on the wire - none of it is invented.
	//
	// ⭐ **The client names the system end to end.** Its stat-name table carries a contiguous block
	// (docs/re/aoc-client-stat-names.tsv, ids 1000066..1000077):
	//
	//   DailyResetTimestamp / WeeklyResetTimestamp / MonthlyResetTimestamp
	//   DailyPvEKills / DailyPvPKills / DailyMinigameWins / DailyQuestsCompleted / DailyUnchainedDungeonsCompleted
	//   DailyDailyCounter / DailyWeeklyCounter / DailyMonthlyCounter
	//   CounterDailyChallenges
	//
	// and its tuning sheet (RDB 1000516) carries a cap for every one of them. The two agree name-for-name, which is
	// what makes this a MODEL rather than a list of counters (#656 established the pairing).
	//
	// ⭐ **And the LDB corroborates the three meta tiers independently**: `DailyMetaCap` 3 / `WeeklyMetaCap` 15 /
	// `MonthlyMetaCap` 60 are exactly the client's own milestone strings *"Completed 3 Daily Challenges"* /
	// *"Completed 15..."* / *"Completed 60..."* (category 100), and the three reward chests escalate to match -
	// Contender's Cache *"basic items"*, Exemplar's Cache *"valuable rewards"*, Paragon's Cache *"rewards of high
	// value"* (items 5063646/47/48, consecutive). Three independent sources, one model.
	//
	// ⚠️ **A cap is a THRESHOLD, not a clamp.** The corpus runs `DailyQuestsCompleted` to 7 and `DailyPvEKills` to 13
	// against caps of 3 and 5, so the counter keeps counting past its cap; what the cap decides is when that
	// category's daily challenge is COMPLETE. (That is the sharpened form of #656's open question, answered here.)
	//
	// ⭐ And two arguments that need no corpus at all, so the reading does not rest on the captures we happen to have
	// (#887):
	//
	//   * **Two of the caps are 1** - `DailyMinigameWinsCap` and `DailyUnchainedDungeonsCompletedCap`. As a clamp
	//     that would mean a player can never win a second minigame or finish a second dungeon in a day, which is
	//     absurd; as a threshold it is exactly right, and it is the shape a one-shot daily has.
	//   * **The client's own declaration ORDER groups them with the meta.** All five per-category caps sit in one
	//     contiguous run of the tweak-declaration table (`FUN_010cb540`, strings at `0x0203c9dc..0x0203ca34`) and the
	//     run continues straight into `DailyMetaCap` / `WeeklyMetaCap` / `MonthlyMetaCap` at `0x0203ca58`. A
	//     per-category clamp has no reason to be filed next to the meta's own caps; a per-category CONTRIBUTION does.
	//
	// ⚠️ `TweakGlobals` reports "no registration stub found" for `DailyPvEKillsCap` and that is not a finding about
	// the tweak - the client declares tweakables through TWO mechanisms (the 5-instruction stub table that script
	// pattern-matches, and this bulk declaration function), and this one only goes through the second. Do not read
	// that script's silence as "the client does not know the name".
	//
	// What is measured on the wire, and therefore what this reproduces:
	//
	//   * ⭐ `DailyQuestsCompleted` ticks when a quest LINE ends - not per quest step. 5 of 5 across two captures: a
	//     completion followed by the next step of the same line does NOT tick (10238->12369 line 241, 10287->10915
	//     line 241, 10928->10830 line 386), while a completion that ends its line and raises `MainQuestUpdateIIR`
	//     DOES (12407, 10915). So the hook is the chain-END completion, which is exactly `CompleteFinal`.
	//     [V2: xxx_mage-demo_login_play_beach-to-enter-tortage_city.tshark.json t=637.6 / t=819.5]
	//     [V2: xxx_mage-necromancer_login_play3.tshark.json t=101.7 / t=213.5 / t=329.0]
	//   * ⭐ When a challenge completes, all FOUR of `DailyDailyCounter`, `DailyWeeklyCounter`, `DailyMonthlyCounter`
	//     and `CounterDailyChallenges` increment by one, together, in ONE `StatUpdateIIR`. Three captures show the
	//     lockstep and none shows them moving apart.
	//     [V2: xxx_mage-demo_login_play_beach-to-enter-tortage_city.tshark.json t=436.4 / t=530.0]
	//
	// ⚠️ **What is NOT modelled, deliberately.** Only the quest category has a measured trigger. The other four
	// (`DailyPvEKills`, `DailyPvPKills`, `DailyMinigameWins`, `DailyUnchainedDungeonsCompleted`) are declared here
	// because the client declares them and the reset must clear them, but nothing increments them: which kill counts
	// toward which is #656's `[V0]` (`DailyPvPKills` moves in a corpus with no PvP in it), and guessing would put a
	// wrong number in front of the player. The three tier CACHES are not granted either - no capture shows the grant,
	// so the reward side is a gap and not an invention.
	public sealed class DailyChallengeService
	{
		// The five per-category counters and the cap at which each one's daily challenge completes. Values are the
		// SHIPPED tuning sheet's, not the exe's fallbacks - #656 measured that every one of the eight differs
		// between the two, so the binary's defaults would have been wrong every time.
		// `--report rdb-tweaks --report-arg Daily`.
		public static readonly IReadOnlyList<(CharacterStatId Counter, int Cap, string CapTweak)> Categories =
		[
			(CharacterStatId.DailyPvEKills, 5, "DailyPvEKillsCap"),
			(CharacterStatId.DailyPvPKills, 20, "DailyPvPKillsCap"),
			(CharacterStatId.DailyMinigameWins, 1, "DailyMinigameWinsCap"),
			(CharacterStatId.DailyQuestsCompleted, 3, "DailyQuestsCompletedCap"),
			(CharacterStatId.DailyUnchainedDungeonsCompleted, 1, "DailyUnchainedDungeonsCompletedCap")
		];

		// The three meta counters, their caps, and the reset instant each is cleared against. `MonthlyMetaCap` 60 is
		// the client's own "Completed 60 Daily Challenges" milestone, which is what makes the triple a ladder.
		public static readonly IReadOnlyList<(CharacterStatId Counter, int Cap, CharacterStatId ResetStamp, TimeSpan Period)> Metas =
		[
			(CharacterStatId.DailyDailyCounter, 3, CharacterStatId.DailyResetTimestamp, TimeSpan.FromDays(1)),
			(CharacterStatId.DailyWeeklyCounter, 15, CharacterStatId.WeeklyResetTimestamp, TimeSpan.FromDays(7)),
			(CharacterStatId.DailyMonthlyCounter, 60, CharacterStatId.MonthlyResetTimestamp, TimeSpan.FromDays(28))
		];

		// The lifetime tally - the one counter in the block that no reset clears (the achievement ladder counts it:
		// "Complete 10/25/50/100/250/500 daily challenges", LDB 55700).
		private const CharacterStatId Lifetime = CharacterStatId.CounterDailyChallenges;

		private readonly ICharacterRepository _database;
		private readonly ILogger _logger;

		public DailyChallengeService(ICharacterRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// A quest LINE ended (QuestProgressionService.CompleteFinal, the chain-end completion retail ticks on).
		public Task RecordQuestLineCompleted(INetworkClient client, uint characterId, DateTimeOffset now)
		{
			return RecordCategoryProgress(client, characterId, CharacterStatId.DailyQuestsCompleted, now);
		}

		// Advance one category counter by one, apply any due reset first, and complete that category's challenge if
		// the step CROSSES its cap. Returns the stats that changed, already persisted and pushed.
		public async Task<IReadOnlyDictionary<CharacterStatId, uint>> RecordCategoryProgress(INetworkClient client, uint characterId, CharacterStatId category, DateTimeOffset now)
		{
			if (Categories.All(c => c.Counter != category))
				throw new ArgumentOutOfRangeException(nameof(category), category, "not a daily-challenge category counter");

			var sheet = (await _database.GetCharacterStats(characterId)).GroupBy(s => s.StatId).ToDictionary(g => g.Key, g => g.First().Value);
			var changed = ApplyDueResets(sheet, now);

			var cap = Categories.First(c => c.Counter == category).Cap;
			var before = sheet.GetValueOrDefault(category);
			var after = before + 1;
			sheet[category] = after;
			changed[category] = after;

			// ⚠️ CROSSES, not "is at or above": the counter runs past its cap (a captured character sits at 7 against
			// a cap of 3), so testing `>=` would complete the same challenge again on every further step.
			if (before < cap && after >= cap)
			{
				_logger.LogInformation("Character {CharacterId}: daily challenge {Category} complete ({After}/{Cap}).", characterId, category, after, cap);
				CompleteChallenge(sheet, changed);
			}

			await Persist(client, characterId, changed);
			return changed;
		}

		// The four counters retail moves together when a challenge completes. Kept separate from the category step so
		// the lockstep is stated once, in the shape the capture shows it.
		private static void CompleteChallenge(Dictionary<CharacterStatId, uint> sheet, Dictionary<CharacterStatId, uint> changed)
		{
			foreach (var stat in Metas.Select(m => m.Counter).Append(Lifetime))
			{
				var value = sheet.GetValueOrDefault(stat) + 1;
				sheet[stat] = value;
				changed[stat] = value;
			}
		}

		// Zero whatever is due, by comparing each meta's own reset instant against now. A character who has never had
		// one stamped gets it stamped without a reset - otherwise every first login would look like a rollover and
		// clear counters the snapshot had just restored.
		//
		// ⚠️ The three timestamps are the one part of this block the corpus NEVER shows (0 occurrences of stat
		// 1000066/67/68 across all 134 captures, where the nine counters occur in 46-72 each). So the unit and epoch
		// are ours: unix SECONDS of the last boundary applied. Nothing observable depends on the choice - the client
		// is never shown these - and it is recorded here rather than hidden. [V0: the reset timestamps' wire form.]
		private Dictionary<CharacterStatId, uint> ApplyDueResets(Dictionary<CharacterStatId, uint> sheet, DateTimeOffset now)
		{
			var changed = new Dictionary<CharacterStatId, uint>();
			var stamp = (uint)now.ToUnixTimeSeconds();

			foreach (var (counter, _, resetStamp, period) in Metas)
			{
				var last = sheet.GetValueOrDefault(resetStamp);
				if (last != 0 && now.ToUnixTimeSeconds() - last < (long)period.TotalSeconds)
					continue;

				if (last != 0)
				{
					sheet[counter] = 0;
					changed[counter] = 0;

					// The DAILY rollover is also what clears the five category counters - they are what "daily" means
					// in every one of their names, and the weekly/monthly metas are tallies OF completed challenges
					// rather than of activity, so nothing else clears them.
					if (period == TimeSpan.FromDays(1))
					{
						foreach (var (category, _, _) in Categories)
						{
							sheet[category] = 0;
							changed[category] = 0;
						}
					}
				}

				sheet[resetStamp] = stamp;
				changed[resetStamp] = stamp;
			}

			return changed;
		}

		// Persist to the stat SHEET (which is what the login snapshot is authored from, so this survives a relog) and
		// push the changed stats as ONE StatUpdateIIR - the shape retail uses, which carries only what moved.
		private async Task Persist(INetworkClient client, uint characterId, IReadOnlyDictionary<CharacterStatId, uint> changed)
		{
			if (changed.Count == 0)
				return;

			await _database.UpsertCharacterStats(characterId, changed);

			var block = new CharacterStatBlock();
			foreach (var (stat, value) in changed)
				block.SetRaw(stat, value);

			await WorldIir.Send(client, StatUpdateCodec.Build(characterId, block));
		}
	}
}

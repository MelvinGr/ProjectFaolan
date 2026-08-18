using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Progression;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Progression
{
	// Character ADVANCEMENT: experience in, levels out, and the whole level-up wire burst that follows one (#209).
	// Every way a character's level can move goes through here - an XP-driven ding on a kill (CombatService) and a GM
	// `.level` set - so the burst has ONE owner.
	//
	// It used to have none. `CombatService.PushLevelUp` built the burst for the kill path while `SetLevelCommand`
	// built a second copy for `.level`, reaching four more senders scattered across the listener partials
	// (`SendLevelUpDings` in .WorldEntry, `SendLevelUpFeedback` in .Buffs, `SendCombatRatings` in .Outbound), and the
	// two copies were kept in step by a comment saying they must not diverge. Levelling is not combat: the ding sends
	// the spell book, the ability grants and the feat-teaching writes, none of which a swing resolution has any
	// business knowing about - and carrying them dragged two `Func<>` seams into CombatService's constructor that
	// nothing else in it used.
	//
	// A kill is one SOURCE of experience, not the definition of it: `QuestDefinition.XpReward` is the next one, and it
	// wires in as a second `ApplyXp` caller rather than a third copy of this burst.
	public sealed class LevelUpService
	{
		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// The wire ops the burst needs from the listener (vitals reseed, rating quad, ability grant, spell learn).
		// Null-safe for tests that construct the service without a listener.
		private readonly ILevelUpOutbound? _outbound;

		// The live world model: a ding must move the player ENTITY's level too, not just the row.
		private readonly WorldSimulation _sim;

		public LevelUpService(IDatabaseRepository database, WorldSimulation sim, ILevelUpOutbound? outbound, ILogger logger)
		{
			_database = database;
			_sim = sim;
			_outbound = outbound;
			_logger = logger;
		}

		// Awards `xp` and advances levels when the bar fills (LevelingResolver), pushing a 0x96B8DC59 stat update so
		// the XP bar (and, on a ding, level + new vitals) move. Experience is within-level progress. The new XP/level
		// are PERSISTED immediately (SetCharacterProgress) so they survive relog instead of waiting on the full-graph
		// disconnect/autosave save (which the player can lose by crashing or which can roll back on a navigation
		// conflict).
		public async Task ApplyXp(INetworkClient client, uint xp)
		{
			var character = client.Session.Character;
			character.Experience += xp;

			var (levelsGained, remaining) = LevelingResolver.Apply(character.Level, character.Experience);
			character.Experience = remaining;

			if (levelsGained > 0)
			{
				character.Level = (ushort)Math.Min(character.Level + levelsGained, LevelingResolver.MaxLevel);
				SyncLiveLevel(client, character.Level);

				// No ceiling to store: the vitals max is DERIVED from (Class, Level), so writing the level IS writing
				// the new max (#578). It used to be a persisted CharacterMaxStats row that only the `.level` GM command
				// ever updated - a combat level-up wrote the level and left the row at its creation value, so the next
				// login re-seeded current health from the old ceiling and the player came back at a fraction of their
				// bar (a level-2 necromancer reading 89/955).
				await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);
				await PushLevelUp(client, character, levelsGained);
				_logger.LogInformation("Progression: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			await _database.SetCharacterProgress(character.Id, character.Experience, character.Level);

			var block = new CharacterStatBlock().SetExperience(character.Level, character.Experience);
			await SendStatUpdate(client, block);
			_logger.LogInformation("Progression: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
		}

		// Sets the character's level outright (the `.level` GM command, a test bootstrap for level-gated features).
		// An INCREASE runs the same burst an XP ding does - dings, ratings, attribute lines, vitals, ability/spell
		// unlocks - so the two paths cannot drift; a level-DOWN has no ding, but still restates the level-derived
		// state the client is now holding a stale copy of.
		public async Task SetLevel(INetworkClient client, ushort level)
		{
			var character = client.Session.Character;
			var previousLevel = character.Level;

			character.Level = level;
			SyncLiveLevel(client, level);
			// A GM `.level` set jumps to the START of the target level, so zero the within-level XP: otherwise stale
			// progress from a higher level shows against the (smaller) new MaxExp and the XP bar renders NEGATIVE on a
			// level-DOWN (the bar's fill = Exp/MaxExp with Exp > MaxExp, or Exp for a level whose threshold is now below
			// the carried total). Fresh level = empty bar.
			character.Experience = 0;
			// No vitals row to refresh: the ceiling is DERIVED from (Class, Level), so setting the level above is
			// already the new max (#578).
			await _database.SaveCharacter(character);

			if (level > previousLevel)
			{
				await PushLevelUp(client, character, (uint)(level - previousLevel));
				return;
			}

			// A level-down (or a set to the same level) is not a ding: no NewLevelIIR, no attribute lines, no unlocks.
			// The vitals ceiling and the rating quad still moved, and the client would otherwise render both at the
			// level it was on until the next relog.
			await PushLevelAndVitals(client, character);
			if (_outbound != null)
				await _outbound.SendCombatRatings(client);
		}

		// The level-up burst. Retail's order, from the combat-levelup capture (frames 9937->9939): the ding EVENTS
		// first, then the rating quad, then the on-screen attribute lines, then the stat block, then the unlocks.
		private async Task PushLevelUp(INetworkClient client, Character character, uint levelsGained)
		{
			var instance = character.Id;
			var previousLevel = (uint)character.Level - levelsGained;

			// The level-up EVENT first (0x6D314743) - fires the client's "Congratulations, you have reached level N!"
			// banner + the level-up visual/audio effect (#209). The banner text is client-side and gated on this
			// packet; the raw Level stat + AttributeChanged lines alone show nothing. Retail sends a SEPARATE event PER
			// level (combat-levelup frame 9937, and Hoxxling's L2/L3/L4 as three), each carrying
			// [newLevel, currentXP, xpThisLevel, xpNextLevel, 1, 8, 0] (fields 5/6/7 constant across every capture).
			for (var lvl = previousLevel + 1; lvl <= character.Level; lvl++)
			{
				var thisXp = LevelingResolver.CumulativeXpForLevel(lvl);
				var nextXp = LevelingResolver.CumulativeXpForLevel(lvl + 1);
				var currentXp = lvl == character.Level
					? thisXp + character.Experience
					: thisXp; // only the final level carries partial XP
				ReadOnlySpan<uint> deltas = [currentXp, thisXp, nextXp, 1u, 8u, 0u];
				await WorldIir.Send(client, NewLevelCodec.Build(instance, lvl, deltas));

				// The combos this class unlocks AT this level, from the class<->combo ACCESS map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the old
				// class-independent level-keyed catalog (casters have no rows); it ships empty until populated from dumps,
				// so this is a no-op today.
				foreach (var combo in await _database.GetClassCombosUnlockedBetween(character.Class, lvl - 1, lvl))
					await WorldIir.Send(client, ComboBookCodec.Build(instance, combo.Id));
			}

			// The rating QUAD is LEVEL-DERIVED (`400 x level - 300`, #685), so the ding invalidates the copy the login
			// snapshot carried - and retail restates it right here: 15 of the 18 captured `NewLevelIIR` events are
			// followed by a quad burst, 11 of them within 0.1 s, i.e. inside this same burst. Without it a character's
			// four combat ratings read the level they logged in at until the next relog.
			if (_outbound != null)
				await _outbound.SendCombatRatings(client);

			// The retail attribute-increase feedback: one AttributeChanged on-screen line per primary that rose - the
			// VISIBLE half of a ding (#209). `levelsGained` covers a multi-level jump (e.g. a `.level 40` set). The
			// growth amounts are provisional (AttributeGrowth) until real per-class tables exist.
			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, character.Level, levelsGained))
				await WorldIir.Send(client, FormatFeedbackCodec.Build(instance, new AttributeChanged(attributeId, amount)));

			await PushLevelAndVitals(client, character);

			// Level-gated ability unlocks (#209/#73): retail grants the abilities that unlock at the new level as
			// ANNOUNCED 0x2052s at the ding (Rest at the L1->L2 ding - Blablabla frame 9937, Blablatest 1357,
			// Stihetfv 685 - and in every L2+ char's login re-grant). The class<->ability map gives the per-class
			// unlock level, so the ding grants are the band newly crossed.
			if (_outbound == null)
				return;

			foreach (var ability in await _database.GetClassAbilitiesUnlockedBetween(character.Class, previousLevel, character.Level))
				await _outbound.AnnounceAbility(client, ability);

			// Level-gated SPELL unlocks (#209/#74): the class's spells whose Spells.Level falls in the newly-crossed
			// range - learned (DispatchLearnSpell) and persisted to CharacterSpells so they ride every relog, exactly
			// like .learnspell / a create-flow starter. Combos are NOT in that set (#298) - they went out above, on the
			// combo book, which is the only place retail puts them.
			var learned = await _database.GetClassSpellsUnlockedBetween(character.Class, previousLevel, character.Level);
			foreach (var spellId in learned)
			{
				await _outbound.LearnSpell(client, spellId);
				await _database.AddCharacterSpell(character.Id, spellId);
			}

			await GrantCastabilityFor(character);
		}

		// Raises the player's live vitals to the new max and refills them (a ding heals), then pushes the new Level +
		// Exp/MaxExp + full vitals in one 0x96B8DC59 update.
		//
		// The vitals ride that block WHOLE - current AND max. This was a `SendVitals(client)` that emitted the three
		// CURRENT ids only, on the belief that "max rides the snapshot": true at world entry, wrong for every later
		// change. Measured in-client (#450): `.level 80` left the client rendering `4285 / 1270` - the new current
		// against the max it had been seeded with 57 levels earlier.
		private async Task PushLevelAndVitals(INetworkClient client, Character character)
		{
			if (_outbound == null)
				return;

			var block = (await _outbound.ReseedVitals(client))
				.ToVitalsBlock()
				// Level AND ApprenticeLevel: the nameplate renders max() of the two, so pushing Level alone left
				// a level-DOWN showing the old level over the health bar while the sheet showed the new one
				// (#472). See CharacterStatBlock.SetLevel.
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);

			await SendStatUpdate(client, block);
		}

		// The LIVE entity's level, not just the row: it is what ramped buff deltas and the attribute rebase read, so
		// leaving it behind would keep resolving them at the level the player entered the world with.
		private void SyncLiveLevel(INetworkClient client, ushort level)
		{
			if (_sim.PlayerWorldEntity(client) is { } leveled)
				leveled.Level = level;
		}

		// What makes a just-learned spell CASTABLE (#298): the class SKILL scaffold, and nothing else.
		//
		// ⛔ This used to ALSO train the feats that teach the newly learned spells, on the reading that the client's
		// criteria evaluator refuses the cast until the character carries the teaching feat (its feat case,
		// `YouCanNotUseYet`). That grant is GONE (#862), because the client trains those feats itself:
		//
		//   * The client's own FEATCLASS table (`FeatClassEntry`, RDB 1000043) marks 3,839 of its 9,916 rows
		//     `Autotrain`, and builds a per-class level -> feat map from them (`FUN_0078c990`, which logs
		//     "Autotrain feat: <id> for class: <class> at level: <level>"). `Spell Unlocking 20-40`,
		//     `<Class> Class Feat NNN` and `Pet Mastery` - everything the grant handed out - are exactly those rows.
		//   * So retail never sends one, and the captures agree with no exception: all 56 feat ids across the four
		//     captured characters are `Autotrain = false`, and the level-80 Necromancer casts all 109 of her spells
		//     carrying 25 tree feats and not one automatic feat.
		//   * Sending one is not merely redundant. `FeatHolder::TrainFeat` (`Feat\FeatHolder.cpp:74`) refuses a
		//     class feat outright and logs "called TrainFeat on class feat : %d ( will break feats! )".
		//
		// The feat list goes on the wire, so training one was inventing content to satisfy a criterion the client
		// satisfies for itself - and it did so at the WRONG level, which is what made it visible: a level-4 test
		// character was carrying `Spell Unlocking 20-40`, `40-60` and `60-80`, feats retail hands over at 30, 45
		// and 75.
		//
		// The scaffold only ever worked for the four classes with a high-level capture (Necromancer, Demonologist,
		// Assassin, Herald of Xotli) - `FullSkillIdsFor` returns null for the other eight. That is a real gap and it
		// is tracked as such; it is not a reason to put feats retail does not send into a character's list.
		//
		// It takes effect on the next world entry: the scaffold rides the spawn's post-IIR frame, so there is no
		// mid-session packet to send and none is invented here.
		//
		// ⚠️ The scaffold in (2) is a CAPTURED character's id list, so what it may grant is the class's CONTENT and
		// never that character's own state. Two bands used to ride along and both showed up in-client on a level-2
		// Necromancer's first relog: the captured L80's class-PROGRESSION entry, which the client reads as the
		// character's level and drew level-80 attributes and health from (Str 71 / Int 231 / HP max 984), and that
		// player's trained TRADESKILL rungs, which made it a Grandmaster weapon- and armoursmith. Both are out of
		// FullSkillIdsByClass now - the progression entry is DERIVED per character at spawn time
		// (PlayerSpawnScaffoldRows.ClassProgressionIdFor) and a profession is trained, not dinged into.
		private async Task GrantCastabilityFor(Character character)
		{
			var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
			if (fullScaffold != null)
				await _database.EnsureCharacterSkills(character.Id, fullScaffold);
		}

		// Pushes a 0x96B8DC59 stat block to the player, addressed to their own handle (shared wire builder on
		// World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, client.Session.Character.Id));
		}
	}
}

using System;
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
	/// <summary>
	///     Character advancement: experience in, levels out, and the whole level-up wire burst that follows one.
	/// </summary>
	/// <remarks>
	///     Every way a character's level can move goes through here - an XP-driven ding on a kill (CombatService) and a
	///     GM <c>.level</c> set - so the burst has one owner and the paths cannot drift. A kill is one source of
	///     experience rather than the definition of it: a further source wires in as another <c>ApplyXp</c> caller, not
	///     as a second copy of the burst.
	/// </remarks>
	public sealed class LevelUpService
	{
		private readonly ICatalogRepository _catalog;
		private readonly ICharacterProgressionRepository _progression;
		private readonly ICharacterRepository _characters;
		private readonly ILogger _logger;

		// The wire ops the burst needs from the listener (vitals reseed, rating quad, ability grant, spell learn).
		// Null-safe for tests that construct the service without a listener.
		private readonly ILevelUpOutbound? _outbound;

		// The live world model: a ding must move the player entity's level too, not just the row.
		private readonly WorldSimulation _sim;

		public LevelUpService(ICatalogRepository catalog, ICharacterProgressionRepository progression, ICharacterRepository characters, WorldSimulation sim, ILevelUpOutbound? outbound, ILogger logger)
		{
			_catalog = catalog;
			_progression = progression;
			_characters = characters;
			_sim = sim;
			_outbound = outbound;
			_logger = logger;
		}

		// Awards `xp` and advances levels when the bar fills (LevelingResolver), pushing a 0x96B8DC59 stat update so
		// the XP bar - and, on a ding, level + new vitals - move. Experience is within-level progress. The new XP and
		// level are persisted immediately (SetCharacterProgress) so they survive a relog rather than waiting on the
		// full-graph disconnect/autosave write, which a crash loses and a navigation conflict can roll back.
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

				// No ceiling to store: the vitals max is derived from (Class, Level), so writing the level is writing
				// the new max.
				await _progression.SetCharacterProgress(character.Id, character.Experience, character.Level);
				await PushLevelUp(client, character, levelsGained);
				_logger.LogInformation("Progression: {Name} gained {Xp} XP and reached level {Level}.", character.Name, xp, character.Level);
				return;
			}

			await _progression.SetCharacterProgress(character.Id, character.Experience, character.Level);

			var block = new CharacterStatBlock().SetExperience(character.Level, character.Experience);
			await SendStatUpdate(client, block);
			_logger.LogInformation("Progression: {Name} gained {Xp} XP ({Exp}/{Max}).", character.Name, xp, character.Experience, LevelingResolver.XpForNextLevel(character.Level));
		}

		// Sets the character's level outright (the `.level` GM command, a test bootstrap for level-gated features).
		// An increase runs the same burst an XP ding does - dings, ratings, attribute lines, vitals, ability and spell
		// unlocks - so the two paths cannot drift; a level-down has no ding, but still restates the level-derived state
		// the client is now holding a stale copy of.
		public async Task SetLevel(INetworkClient client, ushort level)
		{
			var character = client.Session.Character;
			var previousLevel = character.Level;

			character.Level = level;
			SyncLiveLevel(client, level);
			// A GM `.level` set jumps to the start of the target level, so the within-level XP is zeroed: otherwise
			// stale progress from a higher level shows against the smaller new MaxExp and the XP bar renders negative
			// on a level-down (its fill is Exp/MaxExp with Exp > MaxExp). A fresh level means an empty bar.
			character.Experience = 0;
			// No vitals row to refresh: the ceiling is derived from (Class, Level), so setting the level above is
			// already the new max.
			await _characters.SaveCharacter(character);

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

		// The level-up burst. Retail's order, from the combat-levelup capture (frames 9937->9939): the ding events
		// first, then the rating quad, then the on-screen attribute lines, then the stat block, then the unlocks.
		private async Task PushLevelUp(INetworkClient client, Character character, uint levelsGained)
		{
			var instance = character.Id;
			var previousLevel = (uint)character.Level - levelsGained;

			// The level-up event first (0x6D314743) - it fires the client's "Congratulations, you have reached level
			// N!" banner and the level-up visual/audio effect. The banner text is client-side and gated on this packet;
			// the raw Level stat and AttributeChanged lines alone show nothing. Retail sends a separate event per level
			// (combat-levelup frame 9937, and Hoxxling's L2/L3/L4 as three), each carrying
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

				// The combos this class unlocks at this level, from the class-to-combo access map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). The map ships empty until
				// it is populated from dumps, so this is a no-op until then.
				foreach (var combo in await _catalog.GetClassCombosUnlockedBetween(character.Class, lvl - 1, lvl))
					await WorldIir.Send(client, ComboBookCodec.Build(instance, combo.Id));
			}

			// The rating quad is level-derived (`400 x level - 300`), so the ding invalidates the copy the login
			// snapshot carried - and retail restates it right here: 15 of the 18 captured `NewLevelIIR` events are
			// followed by a quad burst, 11 of them within 0.1 s, i.e. inside this same burst. Without it a character's
			// four combat ratings read the level they logged in at until the next relog.
			if (_outbound != null)
				await _outbound.SendCombatRatings(client);

			// The retail attribute-increase feedback: one AttributeChanged on-screen line per primary that rose - the
			// visible half of a ding. `levelsGained` covers a multi-level jump (e.g. a `.level 40` set). The growth
			// amounts are provisional (AttributeGrowth) until real per-class tables exist.
			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, character.Level, levelsGained))
				await WorldIir.Send(client, FormatFeedbackCodec.Build(instance, new AttributeChanged(attributeId, amount)));

			await PushLevelAndVitals(client, character);

			// Level-gated ability unlocks: retail grants the abilities that unlock at the new level as announced
			// 0x2052s at the ding (Rest at the L1->L2 ding - Blablabla frame 9937, Blablatest 1357 - and in every L2+
			// character's login re-grant). The class-to-ability map gives the per-class unlock level, so the ding
			// grants the band newly crossed.
			if (_outbound == null)
				return;

			foreach (var ability in await _catalog.GetClassAbilitiesUnlockedBetween(character.Class, previousLevel, character.Level))
				await _outbound.AnnounceAbility(client, ability);

			// Level-gated spell unlocks: the class's spells whose Spells.Level falls in the newly-crossed range -
			// learned (DispatchLearnSpell) and persisted to CharacterSpells so they ride every relog, exactly like
			// .learnspell or a create-flow starter. Combos are not in that set; they go out above, on the combo book,
			// which is the only place retail puts them.
			var learned = await _catalog.GetClassSpellsUnlockedBetween(character.Class, previousLevel, character.Level);
			foreach (var spellId in learned)
			{
				await _outbound.LearnSpell(client, spellId);
				await _progression.AddCharacterSpell(character.Id, spellId);
			}

			await GrantCastabilityFor(character);
		}

		// Raises the player's live vitals to the new max and refills them (a ding heals), then pushes the new Level +
		// Exp/MaxExp + full vitals in one 0x96B8DC59 update.
		//
		// The vitals ride that block whole - current and max. Emitting the three current ids alone is only correct at
		// world entry, where the max rides the snapshot: measured in-client, `.level 80` then leaves the client
		// rendering `4285 / 1270`, the new current against the max it was seeded with 57 levels earlier.
		private async Task PushLevelAndVitals(INetworkClient client, Character character)
		{
			if (_outbound == null)
				return;

			var block = (await _outbound.ReseedVitals(client))
				.ToVitalsBlock()
				// Level and ApprenticeLevel both: the nameplate renders max() of the two, so pushing Level alone
				// leaves a level-down showing the old level over the health bar while the sheet shows the new one.
				// See CharacterStatBlock.SetLevel.
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);

			await SendStatUpdate(client, block);
		}

		// The live entity's level, not just the row: it is what ramped buff deltas and the attribute rebase read, so
		// leaving it behind would keep resolving them at the level the player entered the world with.
		private void SyncLiveLevel(INetworkClient client, ushort level)
		{
			if (_sim.PlayerWorldEntity(client) is { } leveled)
				leveled.Level = level;
		}

		// What makes a just-learned spell castable: the class skill scaffold, and nothing else.
		//
		// The server must not train the feats that teach those spells, even though the client's criteria evaluator
		// refuses a cast until the character carries the teaching feat (`YouCanNotUseYet`). The client trains them
		// itself:
		//
		//   * Its feat-class table (`FeatClassEntry`, RDB 1000043) marks 3,839 of its 9,916 rows `Autotrain` and builds
		//     a per-class level -> feat map from them (`FUN_0078c990`, which logs "Autotrain feat: <id> for class:
		//     <class> at level: <level>"). `Spell Unlocking 20-40`, `<Class> Class Feat NNN` and `Pet Mastery` are
		//     exactly those rows.
		//   * Retail therefore never sends one, and the captures agree without exception: all 56 feat ids across the
		//     four captured characters are `Autotrain = false`, and the level-80 Necromancer casts all 109 of her
		//     spells carrying 25 tree feats and not one automatic feat.
		//   * `FeatHolder::TrainFeat` (`Feat\FeatHolder.cpp:74`) refuses a class feat outright and logs "called
		//     TrainFeat on class feat : %d ( will break feats! )", so sending one is harmful rather than redundant.
		//
		// The scaffold only covers the four classes with a high-level capture (Necromancer, Demonologist, Assassin,
		// Herald of Xotli) - `FullSkillIdsFor` returns null for the other eight. That gap is not a reason to put feats
		// retail does not send into a character's list.
		//
		// It takes effect on the next world entry: the scaffold rides the spawn's post-IIR frame, so there is no
		// mid-session packet to send and none is invented here.
		//
		// The scaffold is a captured character's id list, so what it may grant is the class's content and never that
		// character's own state. In particular the captured character's class-progression entry (which the client reads
		// as the character's level, and draws level-80 attributes and health from) and their trained tradeskill rungs
		// are out of FullSkillIdsByClass: the progression entry is derived per character at spawn time
		// (PlayerSpawnScaffoldRows.ClassProgressionIdFor) and a profession is trained, not dinged into.
		private async Task GrantCastabilityFor(Character character)
		{
			var fullScaffold = PlayerSpawnScaffoldRows.FullSkillIdsFor(character.Class);
			if (fullScaffold != null)
				await _progression.EnsureCharacterSkills(character.Id, fullScaffold);
		}

		// Pushes a 0x96B8DC59 stat block to the player, addressed to their own handle (shared wire builder on
		// World.VitalsPacket).
		private static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats)
		{
			return client.SendAsync(VitalsPacket.BuildStatUpdate(client, stats, client.Session.Character.Id));
		}
	}
}

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	// A character's starting kit: the class spells it knows, the abilities its bar is laid out from, and that default
	// bar itself (#74/#71).
	//
	// Seeding is CREATION-time work. It used to run on every world entry, from two call sites, each gated on "does the
	// character already have this" - which made every login re-ask three questions whose answer changes exactly once,
	// and made the create path's need for it look like a special case (the create flow reaches the world through a
	// different entry method, so the login-only call missed it). Seeded at creation, world entry only reads.
	//
	// ResolveAbilities stays shared because it is genuinely needed on both sides: creation lays the bar out from it,
	// and every login re-GRANTS the abilities on the wire (the client keeps no persistent kit).
	public static class CharacterLoadout
	{
		private const uint ShortcutTypeAbility = 4;
		private const uint ShortcutTypeSpell = 2;
		private const uint SpecialBarBase = 0x4E20;
		private const uint SecondBarBase = 100; // page 2 / "bar 1" (slot = bar position, page 2 = +100; see ShortcutBarReplyCodec)

		// The character's grantable abilities: its OWN persisted set (CharacterAbility, imported from the retail 0x2052
		// grants) or, for a character that has none yet, the class-starting kit.
		public static async Task<Ability[]> ResolveAbilities(IDatabaseRepository database, ILogger logger, Character character)
		{
			// Mount abilities ride the MOUNT, not the class or the level (#303), so they are unioned in on top of
			// whichever source below supplies the rest. Before this they sat in ClassAbility under Necromancer and
			// Demonologist at level 80 - which is only where they landed because the corpus's two mount owners happen
			// to be a level-80 Necro and a level-80 Demo. A Barbarian with a horse got none; a mountless Necro got six.
			var fromMounts = await database.GetMountAbilities(character.Id);

			var granted = await database.GetCharacterAbilities(character.Id);
			if (granted.Length == 0)
				return [.. Merge(await database.GetClassAbilities(character.Level, character.Class), fromMounts)];

			var abilities = new List<Ability>(granted.Length);
			foreach (var ga in granted)
			{
				var ability = await database.GetAbility(ga.AbilityId);
				if (ability == null) // ids we do not have cataloged are skipped (that bar slot stays empty)
					continue;

				// An identity-only catalog row (named from the client's LDB, never seen granted - #303) has no TEA key,
				// and the 0x2052 body carries that key. Sending one would be a grant the client cannot resolve, so say
				// so rather than putting a null key on the wire.
				if (!ability.IsGrantable)
				{
					logger.LogWarning("Loadout: ability {Id} '{Name}' has no grant key (catalogued from the client's data, never captured) - not granting it.", ability.Id, ability.Name);
					continue;
				}

				abilities.Add(ability);
			}

			return [.. Merge(abilities, fromMounts)];
		}

		// Union by id, keeping the first source's order. The captured characters' own grants ALREADY contain their
		// mount abilities (that is where the catalog learned them), so this is a no-op for them - it matters for a
		// character that acquires a mount later, whose grant list predates the mount.
		private static IEnumerable<Ability> Merge(IEnumerable<Ability> primary, IEnumerable<Ability> extra)
		{
			var seen = new HashSet<uint>();
			foreach (var ability in primary)
				if (seen.Add(ability.Id))
					yield return ability;

			foreach (var ability in extra)
				if (ability.IsGrantable && seen.Add(ability.Id))
					yield return ability;
		}

		// Tells the CLIENT what the character has: the class abilities (0x2052) then its spells (0x200A), on `sender`'s
		// conversation. The client bars each grant itself, answering with an AddShortcut (0x206E) - which is not just
		// cosmetic during creation: the editor connection keys its enter-world handoff on the first of those, so a
		// character granted nothing never leaves the character creator.
		//
		// That is what made this class-dependent instead of universal: only the spell half was sent, so a necromancer
		// barred its two spells and entered the world while a barbarian - granted nothing at all - sat on a black
		// screen after the fade-out, waiting on a signal it had no reason to send. Retail grants both here (Blablabla
		// guardian create: 7x 0x2052 at fr2886 -> 7 AddShortcut -> the world redirect at fr2911).
		public static async Task GrantToClient(INetworkClient client, IDatabaseRepository database, ILogger logger, ProtoEndpoint sender)
		{
			var character = client.Session.Character;

			var abilities = await ResolveAbilities(database, logger, character);
			foreach (var ability in abilities)
				await GameServerListener.SendAddSpecialAbility(client, ability, sender: sender);

			var spells = await database.GetStartingSpells(character.Level, character.Class);
			foreach (var spellId in spells)
			{
				await client.SendAsync(new Rpc(sender, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell, s => s.WriteUInt32(spellId)));
			}

			logger.LogInformation("Loadout: granted '{Name}' (class={Class}) {A} ability(s) + {S} spell(s) for its bar.", character.Name, character.Class, abilities.Length, spells.Length);
		}

		// Seeds a newly created character's class spells and default action bar. Called once, from the create flow,
		// where the class is known.
		public static async Task Seed(IDatabaseRepository database, ILogger logger, Character character)
		{
			// The class's starting spells become KNOWN (persisted), so they ride the character's first snapshot
			// known-list rather than needing a per-login re-announce. NB the known-list is NOT a "highest rank only"
			// set - it carries EVERY rank, which is correct: all ranks are learned and visible, and showing only the
			// top rank is a CLIENT-side UI option. Never filter a grant to a highest-rank set.
			var spells = await database.GetStartingSpells(character.Level, character.Class);
			foreach (var spellId in spells)
				await database.AddCharacterSpell(character.Id, spellId);

			// The abilities are resolved only to lay the bar out; the 0x2052 WIRE grants (+ the combo book) are sent
			// per login, after the FullChar snapshot and 0x2024 ReadyForPlayScreen (TestAccount necro login capture:
			// abilities at frame 192, snapshot at 81). Granting them pre-snapshot had the snapshot reconstruction
			// discard them (abilities are NOT in the snapshot known-list, unlike spells), leaving the abilities tab
			// and every ability shortcut empty.
			var abilities = await ResolveAbilities(database, logger, character);

			// Default bar layout: the player's primary visible action bar is bar 1 (the +100 page), so the full usable
			// kit lands there - the 3 Normal Attacks (left/middle/right) on bar 1 slots 100,101,102, then the class
			// SPELLS right after them on bar 1 (103+). The other starting abilities (toggle-combat / switch-weapon /
			// utility) go on the special bar (0x4E20+). Bar 0 is left empty so the attacks/spells are not split across
			// two bars. The 3 Normal Attacks come back ordered by Ability.Slot, but retail lays them out on bar 1 with
			// the 2nd and 3rd (bar slots 101/102) the other way round, so swap that pair before placing them (#285).
			var normalAttacks = abilities.Where(a => a.Id is >= 0xBBC40 and <= 0xBBC60).ToList();
			if (normalAttacks.Count >= 3)
				(normalAttacks[1], normalAttacks[2]) = (normalAttacks[2], normalAttacks[1]);

			var secondBarSlot = SecondBarBase;
			foreach (var attack in normalAttacks)
				await database.SetCharacterShortcut(character.Id, secondBarSlot++, ShortcutTypeAbility, attack.Id);

			var specialSlot = SpecialBarBase;
			foreach (var ability in abilities.Where(a => a.Id is < 0xBBC40 or > 0xBBC60))
				await database.SetCharacterShortcut(character.Id, specialSlot++, ShortcutTypeAbility, ability.Id);

			// Spells go on bar 1 (the +100 page), after the page-2 normal attacks - matching retail (Herald 0x3006FF at
			// slot 103), so the player sees them on their primary visible bar rather than bar 0. ONE per spell family
			// (highest known rank) so the bar shows distinct icons, not every rank (Ice Strike R1/R2/R3 share an icon,
			// which made a leveled caster's bar look like it just repeated). All ranks are still learned above.
			var barSpells = await database.GetStartingBarSpells(character.Level, character.Class);
			foreach (var spellId in barSpells)
				await database.SetCharacterShortcut(character.Id, secondBarSlot++, ShortcutTypeSpell, spellId);

			logger.LogInformation("Loadout: seeded '{Name}' (class={Class}) with {S} spells, {A} abilities and the default bar (#74/#71).", character.Name, character.Class, spells.Length,
				abilities.Length);
		}
	}
}

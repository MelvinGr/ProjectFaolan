using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Codecs.GameServer;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;
using Faolan.Core.Network.Codecs;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{


		// Ensures the character's LOADOUT on EVERY world entry (#74/#71), not just at creation:
		//  (1) ALWAYS makes the class's spells + abilities KNOWN to the client (the client keeps no persistent kit),
		//      so the spellbook fills and every action-bar slot - default OR imported - resolves to an icon; also
		//      grants whatever spells/abilities the character's existing bar already references.
		//  (2) Seeds the DEFAULT bar layout ONLY when the character has no bar yet (a fresh character), mirroring the
		//      retail create-flow beach loadout - a customised or imported bar is left exactly as it is.
		// Runs at the start of world entry, before GrantCharacterSpells (which re-announces the spells this session);
		// the bar itself restores to the client via ShortcutBarReply.
		private async Task EnsureCharacterLoadout(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			const uint shortcutTypeAbility = 4;
			const uint shortcutTypeSpell = 2;
			const uint specialBarBase = 0x4E20;
			const uint secondBarBase = 100; // page 2 / "bar 1" (slot = bar position, page 2 = +100; see ShortcutBarBuilder)

			// Seed the class's spells ONLY for a character whose spellbook is EMPTY (a freshly-created one). An
			// imported character already carries its REAL spellbook (the snapshot known-list = its actual highest-rank
			// spells), so padding it with the full all-rank class set would bloat it far past what retail shows. The
			// abilities below are still granted every login (they're never in the snapshot known-list, and the client
			// keeps no persistent kit), and the bar's own spells are ensured resolvable further down.
			var spells = (await Database.GetCharacterSpells(character.Id)).Length == 0
				? await Database.GetStartingSpells(character.Level, character.Class)
				: [];
			foreach (var spellId in spells)
				await Database.AddCharacterSpell(character.Id, spellId);

			// The character's ABILITIES are its authoritative per-character set (CharacterAbility, imported from the
			// retail 0x2052 grants) - NOT the bar (the bar is only shortcuts). A freshly-created character with no set
			// gets the class-starting kit. We resolve them here ONLY to seed the default bar layout below; the actual
			// 0x2052 WIRE grants (+ the combo book) are NOT sent here. Retail sends them AFTER the FullChar snapshot and
			// after 0x2024 ReadyForPlayScreen (faolan necro login capture: abilities at frame 192, snapshot at 81),
			// driven by the 0x205f post-ready flush (FlushPendingPost205FFollowUp -> GrantCharacterAbilities/Combos).
			// Granting them pre-snapshot (the old behaviour) had the snapshot reconstruction discard them (abilities are
			// NOT in the snapshot known-list, unlike spells), so the abilities tab + ability-slot shortcuts came up empty.
			var abilities = await GetLoadoutAbilities(character);

			// The BAR layout is only SEEDED when the character has none. An imported or player-customised bar is left
			// exactly as it is. NB the bar is only SHORTCUTS - never the source of truth for what the character KNOWS
			// (that is the snapshot known-list -> CharacterSpells, plus CharacterAbility / CharacterCombo re-granted on
			// login); we never add a bar target to those sets (it "learned" a spell/ability twice when the bar
			// referenced a different id than the known set for the same thing - e.g. two "Quell the Ether (Rank 5)" ids).
			var existingShortcuts = await Database.GetCharacterShortcuts(character.Id);

			if (existingShortcuts.Length > 0)
			{
				Logger.LogInformation("EnsureCharacterLoadout: granted {S} spells + {A} abilities to '{Name}' (class={Class}); kept its existing {N}-slot bar.",
					spells.Length,
					abilities.Length,
					character.Name,
					character.Class,
					existingShortcuts.Length);
				return;
			}

			// Default bar layout: the player's primary visible action bar is bar 1 (the +100 page), so the full usable
			// kit lands there - the 3 Normal Attacks (left/middle/right) on bar 1 slots 100,101,102, then the class
			// SPELLS right after them on bar 1 (103+). The other starting abilities (toggle-combat / switch-weapon /
			// utility) go on the special bar (0x4E20+). Bar 0 is left empty so the attacks/spells are not split across
			// two bars. The 3 Normal Attacks come back ordered by Ability.Slot, but retail lays them out on bar 1 with
			// the 2nd and 3rd (bar slots 101/102) the other way round, so swap that pair before placing them (#285).
			var normalAttacks = abilities.Where(a => a.Id is >= 0xBBC40 and <= 0xBBC60).ToList();
			if (normalAttacks.Count >= 3)
				(normalAttacks[1], normalAttacks[2]) = (normalAttacks[2], normalAttacks[1]);

			var secondBarSlot = secondBarBase;
			foreach (var attack in normalAttacks)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeAbility, attack.Id);

			var specialSlot = specialBarBase;
			foreach (var ability in abilities.Where(a => a.Id is < 0xBBC40 or > 0xBBC60))
				await Database.SetCharacterShortcut(character.Id, specialSlot++, shortcutTypeAbility, ability.Id);

			// Spells go on bar 1 (the +100 page), after the page-2 normal attacks - matching retail (Herald 0x3006FF at
			// slot 103), so the player sees them on their primary visible bar rather than bar 0. ONE per spell family
			// (highest known rank) so the bar shows distinct icons, not every rank (Ice Strike R1/R2/R3 share an icon,
			// which made a leveled caster's bar look like it just repeated). All ranks are still learned above.
			var barSpells = await Database.GetStartingBarSpells(character.Level, character.Class);
			foreach (var spellId in barSpells)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeSpell, spellId);

			Logger.LogInformation("EnsureCharacterLoadout: seeded default bar for '{Name}' (class={Class}): {A} abilities + {S} spells (#74/#71).", character.Name, character.Class, abilities.Length, spells.Length);
		}

		// The abilities to grant a character on login: its authoritative per-character set (CharacterAbility, imported
		// from the retail 0x2052 grants) resolved to Ability rows, or the class-starting kit for a freshly-created
		// character with no persisted set. NOT derived from the bar (the bar is only shortcuts).
		private async Task<Ability[]> GetLoadoutAbilities(Character character)
		{
			var granted = await Database.GetCharacterAbilities(character.Id);
			if (granted.Length == 0)
				return await Database.GetClassAbilities(character.Level, character.Class);

			var abilities = new List<Ability>(granted.Length);
			foreach (var ga in granted)
			{
				var ability = await Database.GetAbility(ga.AbilityId);
				if (ability != null) // ids we do not have cataloged are skipped (that bar slot stays empty)
					abilities.Add(ability);
			}

			return [.. abilities];
		}

		// Grants the character's starting abilities so the player has castable attacks in-world (enables the combat
		// test, TODO #3). The set is the universal starting kit every captured character receives via 0x2052 on
		// login - the `Abilities` table, imported from the retail grants by CaptureAbilityImporter (3 Normal Attack
		// directions + toggle-combat / switch-weapon / utility). Sent on the spawn conversation (same family as the
		// 0x201c handshake), after the world has been built. (Class-specific SPELLS are a separate, not-yet-decoded
		// grant - no capture exercises it; see TODO #3 / docs/archive/opcode-re-plan.md.)
		private async Task GrantCharacterAbilities(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var abilities = await GetLoadoutAbilities(character);
			// Login is a SILENT re-grant: the abilities were already learned (in the create flow / on first learn), so
			// the announce flag is cleared - otherwise the client pops "You have learned a new ability" for the whole
			// kit on every login. Retail does exactly this: the create-flow grants carry the announce byte
			// 0x01, the per-login re-grants carry 0x00 (the ONLY differing byte; see BuildAddSpecialAbilityBody).
			foreach (var ability in abilities)
				await SendAddSpecialAbility(client, ability, false);

			Logger.LogInformation("GrantCharacterAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class, character.Level);
		}

		// Re-emits the character's COMBO book on login as ComboBookIIR grants - the combo counterpart to
		// GrantCharacterAbilities. The book = persisted CharacterCombo rows UNION the class's combos up to its level
		// from the class<->combo ACCESS map (ClassCombo); a MAX-LEVEL imported character never dings, so without this
		// its combo book would be empty. Not in the snapshot.
		private async Task GrantCharacterCombos(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var instance = client.Session.Account.ClientInstance;

			// The combo book = the persisted CharacterCombo rows UNION the class's combos up to its level, from the
			// class<->combo ACCESS map (ClassCombo). This replaces the class-INDEPENDENT LevelComboCatalog, which gave
			// every class the same level-keyed set - so a Necromancer (a caster with no weapon combos) re-"learned"
			// Sweep/Slam/Hell Strikes on every login. ClassCombo has NO rows for casters, and ships empty until
			// populated from dumps, so today this grants only the persisted CharacterCombo rows (the safe default).
			var comboIds = new HashSet<uint>();
			foreach (var combo in await Database.GetCharacterCombos(character.Id))
				comboIds.Add(combo.ComboId);
			foreach (var combo in await Database.GetClassCombos(character.Level, character.Class))
				comboIds.Add(combo.Id);

			foreach (var comboId in comboIds)
			{
				await new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArray(ComboBookIirCodec.Build(instance, comboId)).SendAsync(client);
			}

			if (comboIds.Count > 0)
				Logger.LogInformation("GrantCharacterCombos: emitted {Count} combos to '{Name}' (class={Class}, level {Level}).", comboIds.Count, character.Name, character.Class, character.Level);
		}

		// Re-grants the character's learned spells on login - the spell counterpart to GrantCharacterAbilities (#74).
		// Retail re-sends a caster's whole learned set via DispatchLearnSpell on every world entry (invariant across
		// every login capture: NecroL80 always relearns her 5, DemoL80 his 9); the client silently no-ops spells it
		// already knows, so this is safe to replay each login. The set is the character's CharacterSpells rows (seeded
		// for captured characters by CaptureSpellImporter, grown live by .learnspell). Sent after GrantCharacterAbilities.
		private async Task GrantCharacterSpells(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var spells = await Database.GetCharacterSpells(character.Id);
			foreach (var spell in spells)
				await SendLearnSpell(client, spell.SpellId);

			if (spells.Length > 0)
				Logger.LogInformation("GrantCharacterSpells: re-granted {Count} learned spells to '{Name}' (class={Class}).", spells.Length, character.Name, character.Class);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		private Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052).WriteArray(AbilityGrantBody.BuildAddSpecialAbilityBody(ability, announce)).SendAsync(client);
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// Captures/scripts/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchLearnSpell).WriteArray(AbilityGrantBody.BuildLearnSpellBody(spellId)).SendAsync(client);
		}

	}
}

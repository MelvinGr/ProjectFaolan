using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		internal const int AbilityGrantBodyLength = 81; // 20 u32 fields + the announce byte

		// The 0x2052 ability-grant body carries this same 16-byte pair twice, identical for every ability and across
		// every grant burst in the strickenbla combat capture - a fixed category/owner word, not ability-specific.
		private const uint AbilityGrantConstantA = 0xB290805C;
		private const uint AbilityGrantConstantB = 0x29E627CA;

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

			// Grant the character's ABILITIES from its authoritative per-character set (CharacterAbility, imported from
			// the retail 0x2052 grants) - NOT from the bar (the bar is only shortcuts). A freshly-created character with
			// no set gets the class-starting kit. Granted pre-snapshot so the client can place them when it restores the
			// bar mid-load (abilities aren't in the snapshot known-list). Its COMBO book is emitted the same way.
			var abilities = await GetLoadoutAbilities(character);
			foreach (var ability in abilities)
				await SendAbilityGrant(client, ability, false);
			await GrantCharacterCombos(client);

			// The BAR layout is only SEEDED when the character has none. An imported or player-customised bar is left
			// exactly as it is. NB the bar is only SHORTCUTS - never the source of truth for what the character KNOWS
			// (that is the snapshot known-list -> CharacterSpells, plus CharacterAbility / CharacterCombo re-granted on
			// login); we never add a bar target to those sets (it "learned" a spell/ability twice when the bar
			// referenced a different id than the known set for the same thing - e.g. two "Quell the Ether (Rank 5)" ids).
			var existingShortcuts = await Database.GetCharacterShortcuts(character.Id);

			if (existingShortcuts.Length > 0)
			{
				Logger.LogInformation("EnsureCharacterLoadout: granted {S} spells + {A} abilities to '{Name}' (class={Class}); kept its existing {N}-slot bar.", spells.Length, abilities.Length, character.Name, character.Class, existingShortcuts.Length);
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
			// 0x01, the per-login re-grants carry 0x00 (the ONLY differing byte; see BuildAbilityGrantBody).
			foreach (var ability in abilities)
				await SendAbilityGrant(client, ability, false);

			Logger.LogInformation("GrantCharacterAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class, character.Level);
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
				await new PacketStream().WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier)
					.WriteArray(ComboBookIirCodec.Build(instance, comboId)).SendAsync(client);

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
		private Task SendAbilityGrant(INetworkClient client, Ability ability, bool announce = true)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052).WriteArray(BuildAbilityGrantBody(ability, announce)).SendAsync(client);
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// Captures/scripts/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchLearnSpell).WriteArray(BuildLearnSpellBody(spellId)).SendAsync(client);
		}

		// The 0x2052 body (everything after the opcode), byte-for-byte as retail (locked by AbilityGrantTests against
		// the create-flow capture). See SendAbilityGrant for the field meaning. The final byte is the announce flag:
		// 0x01 = "you learned a new ability" toast (first learn), 0x00 = silent re-grant (login) - the only byte that
		// differs between the create-flow and per-login grants for the same ability across the captures.
		internal static byte[] BuildAbilityGrantBody(Ability ability, bool announce = true)
		{
			return new FaolanStream()
				.WriteUInt32(ability.Slot)
				.WriteUInt32(ability.Id)
				.WriteUInt32(ability.Id)
				.WriteUInt32(0x00000000)
				.WriteUInt32(0x00000001)
				.WriteUInt32(ability.Data0)
				.WriteUInt32(ability.Data1)
				.WriteUInt32(ability.Data2)
				.WriteUInt32(ability.Data3)
				.WriteUInt32(ability.Data4)
				.WriteUInt32(ability.Data1)
				.WriteUInt32(ability.Data2)
				.WriteUInt32(ability.Data3)
				.WriteUInt32(ability.Data4)
				.WriteUInt32(AbilityGrantConstantA)
				.WriteUInt32(AbilityGrantConstantB)
				.WriteUInt32(AbilityGrantConstantA)
				.WriteUInt32(AbilityGrantConstantB)
				.WriteUInt32(0x00000001)
				.WriteUInt32(0x00000000)
				.WriteByte(Convert.ToByte(announce))
				.ToArray();
		}

		// The DispatchLearnSpell 0x200A body: just the big-endian u32 spell id (`i_nSpellID`). Proven byte-exact
		// against the captured grants in LearnSpellTests.
		internal static byte[] BuildLearnSpellBody(uint spellId)
		{
			return new FaolanStream().WriteUInt32(spellId).ToArray();
		}

		// The inverse of BuildAbilityGrantBody: recovers the Ability (Slot/Id/Data0..4) and the announce flag from a
		// 0x2052 body. Validates the fixed words (the duplicated Id, the 0/1 constants, the B290805C/29E627CA pair, the
		// duplicated Data1..4) so a mis-shaped body is rejected rather than yielding garbage. Proven by AbilityGrantTests
		// round-trip against the strickenbla capture. Build- and parse-side share one grammar (TODO §refactor / #92).
		internal static bool TryParseAbilityGrantBody(ReadOnlySpan<byte> body, out Ability ability, out bool announce)
		{
			ability = new Ability();
			announce = false;
			if (body.Length != AbilityGrantBodyLength)
				return false;

			Span<uint> f = stackalloc uint[20];
			for (var i = 0; i < 20; i++)
				f[i] = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i * 4, 4));

			var slot = f[0];
			var id = f[1];
			var data0 = f[5];
			var data1 = f[6];
			var data2 = f[7];
			var data3 = f[8];
			var data4 = f[9];

			// Structural invariants: the duplicated Id, the 0/1 constants, the repeated Data1..4 and the constant pair.
			if (f[2] != id || f[3] != 0x00000000 || f[4] != 0x00000001)
				return false;
			if (f[10] != data1 || f[11] != data2 || f[12] != data3 || f[13] != data4)
				return false;
			if (f[14] != AbilityGrantConstantA || f[15] != AbilityGrantConstantB || f[16] != AbilityGrantConstantA || f[17] != AbilityGrantConstantB)
				return false;
			if (f[18] != 0x00000001 || f[19] != 0x00000000)
				return false;

			var announceByte = body[80];
			if (announceByte > 1)
				return false;

			ability = new Ability
			{
				Slot = slot,
				Id = id,
				Data0 = data0,
				Data1 = data1,
				Data2 = data2,
				Data3 = data3,
				Data4 = data4
			};
			announce = announceByte == 1;
			return true;
		}
	}
}

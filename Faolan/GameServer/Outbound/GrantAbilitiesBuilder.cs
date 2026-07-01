using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
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

		// The persistent self-buff every character carries in-world (the protective "shield" buff, #221). Re-applied on
		// each login AND playfield change: it lives in FlushPendingPost205FFollowUp next to the ability/spell re-grants,
		// and the teleport/changemap re-entry re-arms that flush, so zoning re-asserts it too. Class-agnostic - every
		// character gets it. Applied via the proven ApplySpell (MagicBuff 0x6B87E316) self-buff path, so it also lands in
		// ActiveBuffs (.buffs / CancelBuff / expiry).
		//
		// CAPTURE-PROVEN spell id + payload (#221): the starting self-buff is wire id 0x000B9000 - base 0x0B90 (2960),
		// apply rank 0x00 ((base<<8)|rank). It is applied to EVERY freshly-created character on the beach in all 11
		// create captures (Captures/*_create.tshark.json - Necro/Guardian/Assassin/Barbarian/BearShaman/Herald, etc.),
		// so it is class-agnostic, exactly as this re-apply assumes. (Earlier this was an INTERIM guess - Scythe Shield
		// 0x02387E00 - on the false premise the id was in no capture; it is in every create capture.)
		private const uint ShieldBuffSpellId = 0x000B9000;

		// The captured MagicBuff effect payload for 0x000B9000 (the 54 bytes after applyFrom in the create-capture
		// ApplySpell, e.g. Stickienecro_mage-necromancer_create pkt#284). Only the trailing u32 (@50) is a per-cast
		// counter/timestamp - kept as captured; the client doesn't validate it for a self-buff.
		private static readonly byte[] ShieldBuffPayload =
		[
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x08, 0xA8, 0x15, 0xAD
		];

		// Seeds a fresh character's starting loadout the first time it enters the world with an empty action bar
		// (#74/#71): the class's level-1 SPELLS (persisted to CharacterSpells so GrantStartingSpells sends them this
		// session AND re-grants them every login) and the default action-bar layout (CharacterShortcuts). Mirrors what
		// the retail create flow places on the beach - harvested per class from the create captures (Necromancer
		// 0x125D9B/0x42CA77, Herald 0x3006FF; physical classes get no L1 spells). Idempotent: gated on an empty
		// CharacterShortcuts set, so a player's customised bar is never overwritten. Runs at the start of world entry,
		// before GrantStartingSpells (which then sends the spells this session). The bar restores via ShortcutBarReply.
		private async Task EnsureStartingLoadout(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var existingShortcuts = await Database.GetCharacterShortcuts(character.Id);
			if (existingShortcuts.Length > 0)
				return; // already seeded, or the player has placed/customised the bar - leave it alone

			const uint shortcutTypeAbility = 4;
			const uint shortcutTypeSpell = 2;
			const uint specialBarBase = 0x4E20;
			const uint secondBarBase = 100; // page 2 / "bar 1" (slot = bar position, page 2 = +100; see ShortcutBarBuilder)

			// Persist the class's starting spells. GrantStartingSpells (called right after this in SendWorldEntry) then
			// reads CharacterSpells, sends them, and queues the post-ready re-announce - so the freshly-seeded spells
			// ride the same path as an existing character's learned set, on this login and every relog.
			var spells = await Database.GetStartingSpells(character.Level, (uint)character.Class);
			foreach (var spellId in spells)
				await Database.AddCharacterSpell(character.Id, spellId);

			// Default bar layout: the player's primary visible action bar is bar 1 (the +100 page), so the full usable
			// kit lands there - the 3 Normal Attacks (left/middle/right) on bar 1 slots 100,101,102, then the class
			// SPELLS right after them on bar 1 (103+). The other starting abilities (toggle-combat / switch-weapon /
			// utility) go on the special bar (0x4E20+). Bar 0 is left empty so the attacks/spells are not split across
			// two bars. (Retail's create capture also keeps a copy of the attacks on bar 0, but the player wants the
			// L/M/R attacks + spells together on bar 1.)
			var abilities = await Database.GetStartingAbilities(character.Level, (uint)character.Class);

			// The 3 Normal Attacks come back ordered by Ability.Slot, but retail lays them out on bar 1 with the
			// 2nd and 3rd (bar slots 101/102) the other way round, so swap that pair before placing them (#285).
			var normalAttacks = abilities.Where(a => a.Id is >= 0xBBC40 and <= 0xBBC60).ToList();
			if (normalAttacks.Count >= 3)
				(normalAttacks[1], normalAttacks[2]) = (normalAttacks[2], normalAttacks[1]);

			var secondBarSlot = secondBarBase;
			foreach (var attack in normalAttacks)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeAbility, attack.Id);

			var specialSlot = specialBarBase;
			foreach (var ability in abilities)
			{
				// Normal attacks were placed (in retail order) just above; here only the special-bar abilities get a slot.
				var isNormalAttack = ability.Id is >= 0xBBC40 and <= 0xBBC60;
				if (!isNormalAttack)
					await Database.SetCharacterShortcut(character.Id, specialSlot++, shortcutTypeAbility, ability.Id);

				// Grant the ability NOW (pre-snapshot) so the client knows it when it restores the freshly-seeded bar
				// mid-load and can place it - otherwise the post-0x2024 GrantStartingAbilities arrives AFTER the bar
				// restore and the slot renders empty. Abilities are NOT carried in the snapshot known-list (the Faolanx
				// fresh-necro capture's list is spells only, type 2 - no type-5 ability records), so unlike the SPELLS
				// (which the known-list now lists, #74) the abilities still need this pre-snapshot grant for bar
				// placement. Silent: the post-ready GrantStartingAbilities re-grants the whole kit anyway.
				await SendAbilityGrant(client, ability, false);
			}

			// Spells go on bar 1 (the +100 page), after the page-2 normal attacks - matching retail (Herald 0x3006FF at
			// slot 103), so the player sees them on their primary visible bar rather than bar 0.
			foreach (var spellId in spells)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeSpell, spellId);

			Logger.LogInformation("EnsureStartingLoadout: seeded default bar for '{Name}' (class={Class}): {A} abilities + {S} spells (#74/#71).", character.Name, character.Class, abilities.Length, spells.Length);
		}

		// Grants the character's starting abilities so the player has castable attacks in-world (enables the combat
		// test, TODO #3). The set is the universal starting kit every captured character receives via 0x2052 on
		// login - the `Abilities` table, imported from the retail grants by CaptureAbilityImporter (3 Normal Attack
		// directions + toggle-combat / switch-weapon / utility). Sent on the spawn conversation (same family as the
		// 0x201c handshake), after the world has been built. (Class-specific SPELLS are a separate, not-yet-decoded
		// grant - no capture exercises it; see TODO #3 / docs/archive/opcode-re-plan.md.)
		private async Task GrantStartingAbilities(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var abilities = await Database.GetStartingAbilities(character.Level, (uint)character.Class);
			// Login is a SILENT re-grant: the abilities were already learned (in the create flow / on first learn), so
			// the announce flag is cleared - otherwise the client pops "You have learned a new ability" for the whole
			// starting kit on every login. Retail does exactly this: the create-flow grants carry the announce byte
			// 0x01, the per-login re-grants carry 0x00 (the ONLY differing byte; see BuildAbilityGrantBody).
			foreach (var ability in abilities)
				await SendAbilityGrant(client, ability, false);

			Logger.LogInformation("GrantStartingAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class, character.Level);
		}

		private async Task GrantStartingBuffs(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			// Self-buff: applyTo == applyFrom == the player's own handle (== ClientInstance == Character.Id), which is
			// also what ApplySpell keys the ActiveBuffs registration on. Use the captured payload so the wire matches
			// the retail starting buff byte-for-byte (modulo the per-cast trailing counter).
			var self = client.Session.Account.ClientInstance;
			await ApplySpell(client, self, self, ShieldBuffSpellId, ShieldBuffPayload);
			Logger.LogInformation("GrantStartingBuffs: applied the shield self-buff 0x{Spell:X8} to '{Name}'.", ShieldBuffSpellId, character.Name);
		}

		// Re-grants the character's learned spells on login - the spell counterpart to GrantStartingAbilities (#74).
		// Retail re-sends a caster's whole learned set via DispatchLearnSpell on every world entry (invariant across
		// every login capture: NecroL80 always relearns her 5, DemoL80 his 9); the client silently no-ops spells it
		// already knows, so this is safe to replay each login. The set is the character's CharacterSpells rows (seeded
		// for captured characters by CaptureSpellImporter, grown live by .learnspell). Sent after GrantStartingAbilities.
		private async Task GrantStartingSpells(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var spells = await Database.GetCharacterSpells(character.Id);
			foreach (var spell in spells)
				await SendLearnSpell(client, spell.SpellId);

			if (spells.Length > 0)
				Logger.LogInformation("GrantStartingSpells: re-granted {Count} learned spells to '{Name}' (class={Class}).", spells.Length, character.Name, character.Class);
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

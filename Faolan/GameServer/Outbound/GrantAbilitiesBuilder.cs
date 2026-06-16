using System;
using System.Threading.Tasks;
using Faolan.Core.Data;
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
		// The 0x2052 ability-grant body carries this same 16-byte pair twice, identical for every ability and across
		// every grant burst in the strickenbla combat capture - a fixed category/owner word, not ability-specific.
		private const uint AbilityGrantConstantA = 0xB290805C;
		private const uint AbilityGrantConstantB = 0x29E627CA;

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
				await SendAbilityGrant(client, ability, announce: false);

			Logger.LogInformation("GrantStartingAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class, character.Level);
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

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// Captures/decode_learn_spell.py, byte-exact in LearnSpellTests). The client pops the "learned a new spell"
		// toast itself. (The sibling DispatchForgetSpell 0x200B / RemoveSpecialAbility 0x2053 / UntrainSingleFeat
		// 0x2011 have no capture oracle yet, so they are not built - see #73.)
		private Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchLearnSpell)
				.WriteArray(BuildLearnSpellBody(spellId))
				.SendAsync(client);
		}

		// The DispatchLearnSpell 0x200A body: just the big-endian u32 spell id (`i_nSpellID`). Proven byte-exact
		// against the captured grants in LearnSpellTests.
		internal static byte[] BuildLearnSpellBody(uint spellId)
		{
			return new FaolanStream()
				.WriteUInt32(spellId)
				.ToArray();
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		private Task SendAbilityGrant(INetworkClient client, Ability ability, bool announce = true)
		{
			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052)
				.WriteArray(BuildAbilityGrantBody(ability, announce))
				.SendAsync(client);
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

		internal const int AbilityGrantBodyLength = 81; // 20 u32 fields + the announce byte

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
				f[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.Slice(i * 4, 4));

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
			if (f[14] != AbilityGrantConstantA || f[15] != AbilityGrantConstantB ||
			    f[16] != AbilityGrantConstantA || f[17] != AbilityGrantConstantB)
				return false;
			if (f[18] != 0x00000001 || f[19] != 0x00000000)
				return false;

			var announceByte = body[80];
			if (announceByte > 1)
				return false;

			ability = new Ability { Slot = slot, Id = id, Data0 = data0, Data1 = data1, Data2 = data2, Data3 = data3, Data4 = data4 };
			announce = announceByte == 1;
			return true;
		}
	}
}

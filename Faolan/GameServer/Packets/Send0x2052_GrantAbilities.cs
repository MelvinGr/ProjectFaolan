using System;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
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

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantspell / future
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
	}
}

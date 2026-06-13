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
			foreach (var ability in abilities)
				await SendAbilityGrant(client, ability);

			Logger.LogInformation("GrantStartingAbilities: granted {Count} abilities to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class, character.Level);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row, byte-for-byte matching the retail capture
		// (strickenbla combat capture, e.g. pkt#399 for 769094): [slot][id][id][0][1][data0][data1..4][data1..4]
		// [constA][constB][constA][constB][1][0][flag]. The leading word is the ability's Slot (template word) and the
		// repeated data quad is its identity (already proven by the cast path).
		private Task SendAbilityGrant(INetworkClient client, Ability ability)
		{
			return new PacketStream()
				.WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052)
				.WriteArray(BuildAbilityGrantBody(ability))
				.SendAsync(client);
		}

		// The 0x2052 body (everything after the opcode), byte-for-byte as retail (locked by AbilityGrantTests against
		// the strickenbla capture's pkt#399). See SendAbilityGrant for the field meaning.
		internal static byte[] BuildAbilityGrantBody(Ability ability)
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
				.WriteByte(0x01)
				.ToArray();
		}
	}
}

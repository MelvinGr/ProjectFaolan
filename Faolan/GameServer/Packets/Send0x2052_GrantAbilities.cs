using System;
using System.Threading.Tasks;
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
		// test, TODO #3). The set is data-driven: ClassAbilities lists "<id>_<level>" pairs per class/level (e.g. the
		// two universal melee attacks 769094/769099), and each id resolves to an Ability row for its identity quad.
		// Sent on the spawn conversation (same family as the 0x201c handshake), after the world has been built.
		private async Task GrantStartingAbilities(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var classAbility = await Database.GetClassAbility((uint)character.Class, character.Level);
			if (classAbility == null || string.IsNullOrWhiteSpace(classAbility.Abilities))
			{
				Logger.LogInformation("GrantStartingAbilities: no ClassAbilities row for class={Class} level={Level}; granting nothing.", character.Class, character.Level);
				return;
			}

			var granted = 0;
			foreach (var token in classAbility.Abilities.Split('-', StringSplitOptions.RemoveEmptyEntries))
			{
				// token = "<abilityId>_<level>" (the trailing level is the C++ const_abilitys grant level, unused here).
				var idText = token.Split('_', 2)[0];
				if (!uint.TryParse(idText, out var abilityId))
					continue;

				var ability = await Database.GetAbility(abilityId);
				if (ability == null)
				{
					Logger.LogWarning("GrantStartingAbilities: ability {AbilityId} listed for class={Class} not found in Abilities.", abilityId, character.Class);
					continue;
				}

				await SendAbilityGrant(client, ability);
				granted++;
			}

			Logger.LogInformation("GrantStartingAbilities: granted {Count} abilities to '{Name}' (class={Class}, level={Level}).", granted, character.Name, character.Class, character.Level);
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row, byte-for-byte matching the retail capture
		// (strickenbla combat capture, e.g. pkt#399 for 769094): [slot][id][id][0][1][data0][data1..4][data1..4]
		// [constA][constB][constA][constB][1][0][flag]. The leading word is the ability's Slot (template word) and the
		// repeated data quad is its identity (already proven by the cast path).
		private Task SendAbilityGrant(INetworkClient client, Ability ability)
		{
			return new PacketStream().WriteHeader(LoginWorld201CSender, LoginWorld201CReceiver(client), (ushort)0x2052)
				.WriteArray(BuildAbilityGrantBody(ability))
				.SendAsync(client);
		}

		// The 0x2052 body (everything after the opcode), byte-for-byte as retail (locked by AbilityGrantTests against
		// the strickenbla capture's pkt#399). See SendAbilityGrant for the field meaning.
		internal static byte[] BuildAbilityGrantBody(Ability ability)
		{
			return new Faolan.Core.Data.FaolanStream()
				.WriteUInt32(ability.Slot)
				.WriteUInt32(ability.Id)
				.WriteUInt32(ability.Id)
				.WriteUInt32(0x00000000)
				.WriteUInt32(0x00000001)
				.WriteUInt32(ability.Data0)
				.WriteUInt32(ability.Data1).WriteUInt32(ability.Data2).WriteUInt32(ability.Data3).WriteUInt32(ability.Data4)
				.WriteUInt32(ability.Data1).WriteUInt32(ability.Data2).WriteUInt32(ability.Data3).WriteUInt32(ability.Data4)
				.WriteUInt32(AbilityGrantConstantA).WriteUInt32(AbilityGrantConstantB)
				.WriteUInt32(AbilityGrantConstantA).WriteUInt32(AbilityGrantConstantB)
				.WriteUInt32(0x00000001)
				.WriteUInt32(0x00000000)
				.WriteByte((byte)0x01)
				.ToArray();
		}
	}
}

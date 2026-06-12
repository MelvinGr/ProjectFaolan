using System.Threading.Tasks;
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
		private const uint CharacterWireType = 0x0000C350;

		// 0x2550C99A GuildUpdateIIR_c - sets the entity's in-world guild tag (the guild name under the player
		// name). Retail sends exactly one when the player joins a guild, and the wire layout is
		//   [subop 2550C99A][ID32 char][u16 0][string guildName]
		// (hoxxling guild capture; an empty name clears the tag). Sent on login for guilded characters and
		// pushed live by the CsPlayerAgent guild-accept flow through the dispatcher seam.
		internal static byte[] BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			var ps = new PacketStream();
			ps
				.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), 0x00002000)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.GuildUpdateIIR);
					s.WriteUInt32(CharacterWireType);
					s.WriteUInt32(characterId);
					s.WriteUInt16(0);
					s.WriteString(guildName);
				});
			return ps
				.WriteLengthHash()
				.ToArray();
		}

		// IGameServerProvider seam: pushes the guild tag onto the character's live world connection, if it is
		// connected to this listener. Used by the CsPlayerAgent guild flow (accept/create/leave) so the in-world
		// tag updates without a relog.
		public async Task<bool> SendGuildTag(uint characterId, string guildName)
		{
			var client = ClientForCharacter(characterId);
			if (client?.Session?.Account == null)
				return false;

			await client.SendAsync(BuildGuildTag(client, characterId, guildName ?? ""));
			Logger.LogInformation("SendGuildTag: set in-world guild tag of {CharacterId:X8} to \"{GuildName}\".", characterId, guildName);
			return true;
		}
	}
}

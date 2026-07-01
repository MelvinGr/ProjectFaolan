using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core;
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
		private const uint CharacterWireType = EntityClass.Character;

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

		// 0x2550C99A GuildUpdateIIR_c - sets the entity's in-world guild tag (the guild name under the player
		// name). Retail sends exactly one when the player joins a guild, and the wire layout is
		//   [subop 2550C99A][ID32 char][u16 0][string guildName]
		// (hoxxling guild capture; an empty name clears the tag). Sent on login for guilded characters and
		// pushed live by the CsPlayerAgent guild-accept flow through the dispatcher seam.
		internal static byte[] BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			var ps = new PacketStream();
			ps.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), GameServerCarrierOpcodes.IirCarrier).WriteArrayUInt32Length(s => WriteGuildTagBody(s, characterId, guildName));
			return ps.WriteLengthHash().ToArray();
		}

		// The inverse of WriteGuildTagBody: recovers (characterId, guildName) from a GuildUpdateIIR body. Validates the
		// sub-opcode + C350 wire type and the string length prefix. Proven by the GuildPacketTests round-trip.
		internal static bool TryParseGuildTagBody(ReadOnlySpan<byte> body, out uint characterId, out string guildName)
		{
			characterId = 0;
			guildName = string.Empty;
			if (body.Length < 16) // subop(4) + type(4) + char(4) + u16 0(2) + u16 str-len(2)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000RespondsOpcodes.GuildUpdateIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != CharacterWireType)
				return false;

			characterId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			// body[12..14] is the constant u16 0; the WriteString length prefix follows at [14..16], then the name.
			int len = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(14, 2));
			if (body.Length != 16 + len)
				return false;

			guildName = Encoding.UTF8.GetString(body.Slice(16, len));
			return true;
		}

		// The IIR body (the bytes WriteArrayUInt32Length wraps): [subop 2550C99A][0000C350][char][u16 0][string name].
		// Factored out so it can be built and round-tripped without a live client (GuildPacketTests).
		internal static void WriteGuildTagBody(FaolanStream s, uint characterId, string guildName)
		{
			s.WriteUInt32(GameServer0X2000RespondsOpcodes.GuildUpdateIIR);
			s.WriteUInt32(CharacterWireType);
			s.WriteUInt32(characterId);
			s.WriteUInt16(0);
			s.WriteString(guildName);
		}
	}
}

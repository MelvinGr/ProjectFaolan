using System;
using System.Buffers.Binary;
using System.Text;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
{
	// The GuildUpdateIIR_c (0x2550C99A) body - sets the entity's in-world guild tag (the guild name under the player
	// name). Retail sends exactly one when the player joins a guild; the wire layout is
	//   [subop 2550C99A][ID32 0xC350][char][u16 0][string guildName]
	// (hoxxling guild capture; an empty name clears the tag). Extracted from the GameServerListener.GuildTag partial
	// so it builds + round-trips without a live client (GuildPacketTests). The listener still owns the packet framing
	// (WriteHeader with the world endpoints) and the live send.
	public sealed record GuildTagBody(uint CharacterId, string GuildName) : ICodec<GuildTagBody>
	{


		public void Write(FaolanStream s)
		{
			s.WriteUInt32(GameServer0X2000Opcodes.GuildUpdateIIR);
			s.WriteUInt32(EntityClass.Character);
			s.WriteUInt32(CharacterId);
			s.WriteUInt16(0);
			s.WriteString(GuildName);
		}

		public static GuildTagBody Read(FaolanStream s)
		{
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350 marker
			var characterId = s.ReadUInt32();
			s.ReadUInt16(); // constant 0
			return new GuildTagBody(characterId, s.ReadString());
		}

		// Zero-copy inverse over a raw body: recovers (characterId, guildName), validating the sub-opcode + C350 wire
		// type and the string length prefix. False on any mismatch. Proven by the GuildPacketTests round-trip.
		public static bool TryParse(ReadOnlySpan<byte> body, out uint characterId, out string guildName)
		{
			characterId = 0;
			guildName = string.Empty;
			if (body.Length < 16) // subop(4) + type(4) + char(4) + u16 0(2) + u16 str-len(2)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(0, 4)) != (uint)GameServer0X2000Opcodes.GuildUpdateIIR)
				return false;
			if (BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != EntityClass.Character)
				return false;

			characterId = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(8, 4));
			// body[12..14] is the constant u16 0; the WriteString length prefix follows at [14..16], then the name.
			int len = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(14, 2));
			if (body.Length != 16 + len)
				return false;

			guildName = Encoding.UTF8.GetString(body.Slice(16, len));
			return true;
		}
	}
}

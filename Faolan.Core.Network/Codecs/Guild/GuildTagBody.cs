using System;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// The GuildUpdateIIR_c (0x2550C99A) body - sets the entity's in-world guild tag (the guild name under the player
	// name). Retail sends exactly one when the player joins a guild; the wire layout is
	//   [subop 2550C99A][ID32 0xC350][char][u16 0][string guildName]
	// (hoxxling guild capture; an empty name clears the tag). Extracted from the GameServerListener.GuildTag partial
	// so it builds + round-trips without a live client (GuildPacketTests). The listener still owns the packet framing
	// (WriteHeader with the world endpoints) and the live send.
	public sealed record GuildTagBody(uint CharacterId, string GuildName) : ICodec<GuildTagBody>
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GuildUpdateIIR;

		public static GuildTagBody Read(FaolanStream s)
		{
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350 marker
			var characterId = s.ReadUInt32();
			s.ReadUInt16(); // constant 0
			return new GuildTagBody(characterId, s.ReadString());
		}

		public void Write(FaolanStream s)
		{
			s.WriteUInt32(GameServer0X2000Opcodes.GuildUpdateIIR);
			s.WriteUInt32(EntityClass.Character);
			s.WriteUInt32(CharacterId);
			s.WriteUInt16(0);
			s.WriteString(GuildName);
		}

		// Validating inverse over a raw body: recovers (characterId, guildName), rejecting a mis-routed body.
		//
		// Implemented ON Read rather than re-parsing by fixed offsets. It used to hand-roll the same layout with
		// BinaryPrimitives at 0/4/8/14 - the 8 -> 14 jump silently stepping over the constant u16 - and decoded the
		// name with Encoding.UTF8 while Read used the stream helper, so the two halves of one codec disagreed about
		// the string encoding (wire strings are Latin1; see FaolanStream.ReadString).
		public static bool TryParse(ReadOnlySpan<byte> body, out uint characterId, out string guildName)
		{
			characterId = 0;
			guildName = string.Empty;
			if (body.Length < 16) // subop(4) + type(4) + char(4) + u16 0(2) + u16 str-len(2)
				return false;

			var s = new FaolanStream(body.ToArray());
			if (s.ReadUInt32() != (uint)GameServer0X2000Opcodes.GuildUpdateIIR)
				return false;
			if (s.ReadUInt32() != EntityClass.Character)
				return false;

			s.Position = 0;
			var parsed = Read(s);
			if (s.Position != body.Length) // trailing bytes - not a well-formed tag body
				return false;

			characterId = parsed.CharacterId;
			guildName = parsed.GuildName;
			return true;
		}
	}
}

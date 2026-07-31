using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// The GuildUpdateIIR_c (0x2550C99A) body - sets the entity's in-world guild tag (the guild name under the player
	// name). Retail sends exactly one when the player joins a guild; the wire layout is
	//   [subop 2550C99A][ID32 0xC350][char][u16 0][string guildName]
	// (hoxxling guild capture; an empty name clears the tag). Extracted from the GameServerListener.GuildTag partial
	// so it builds + round-trips without a live client (GuildPacketTests). The listener still owns the packet framing
	// (WriteHeader with the world endpoints) and the live send.
	public static class GuildUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.GuildUpdateIIR;

		public static byte[] Build(GuildUpdateIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string guildName)
		{
			return Build(new GuildUpdateIIR(characterId, guildName));
		}

		// Strict inverse of Build - a mis-routed body (wrong sub-opcode, wrong entity class, trailing bytes) throws
		// rather than decoding into something plausible. It used to hand-roll the same layout with BinaryPrimitives at
		// 0/4/8/14 - the 8 -> 14 jump silently stepping over the constant u16 - and decoded the name with
		// Encoding.UTF8 while the writer used the stream helper, so the two halves of one codec disagreed about the
		// string encoding (wire strings are Latin1; see FaolanStream.ReadString).
		public static GuildUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "the GuildUpdateIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var characterId = r.ReadU32Be();
			r.ReadU16Be(); // constant 0

			return new GuildUpdateIIR(characterId, r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, GuildUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId).WriteUInt16(0).WriteString(v.GuildName);
		}
	}

	public sealed record GuildUpdateIIR(uint CharacterId, string GuildName);
}

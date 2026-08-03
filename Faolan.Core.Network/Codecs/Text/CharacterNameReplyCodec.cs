using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	// DispatchCharacterNameReply (GameCharInterface) - resolves a bare character INSTANCE to a display name, in answer
	// to the client's `RequestCharName(ID32::InstanceType nCharID)` (GameCharAgent 0x2001, whose body is 4 bytes
	// because `ID32::InstanceType` is the instance alone).
	//   [u32 class=0xC350][u32 characterId][name s16]
	// The reply's `i_c` prefixes make both arguments full ID32s, hence the class word this side (#463).
	//
	// [V0] Built from the client's method signature - no capture of the reply exists. NEEDS in-client verification.
	// Moved in from `GameServerListener.Inbound`'s inline stream build in #530 wave 7.
	public static class CharacterNameReplyCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchCharacterNameReply;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterNameReply value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static CharacterNameReply Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterNameReplyCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static CharacterNameReply Read(ref SpanReader r)
		{
			r.ExpectU32Be(EntityClass.Character, "character entity class");

			return new CharacterNameReply(r.ReadU32Be(), r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, CharacterNameReply v)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId).WriteString(v.Name);
		}
	}

	// DispatchCharacterNameReply.
	public sealed record CharacterNameReply(uint CharacterId, string Name);
}

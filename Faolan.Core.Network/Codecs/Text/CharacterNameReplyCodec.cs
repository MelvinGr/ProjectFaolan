using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     <c>DispatchCharacterNameReply</c> - resolves a bare character instance to a display name, in answer to the
	///     client's <c>RequestCharName(ID32::InstanceType nCharID)</c>, whose body is 4 bytes because
	///     <c>ID32::InstanceType</c> is the instance alone. The reply is
	///     <c>[u32 entityClass][u32 characterId][name s16]</c>.
	/// </summary>
	/// <remarks>
	///     The reply's <c>i_c</c> prefixes make both arguments full ID32s, hence the class word on this side. Built from
	///     the client's method signature - no capture of the reply exists. [V0]
	/// </remarks>
	public static class CharacterNameReplyCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchCharacterNameReply;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchCharacterNameReply body.</summary>
	public sealed record CharacterNameReply(uint CharacterId, string Name);
}

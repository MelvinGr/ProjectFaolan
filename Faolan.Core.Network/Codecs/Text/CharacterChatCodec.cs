using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     s2c <c>CharacterChat</c> - proximity "say", spoken by one entity and delivered to the clients in range:
	///     <c>[u32 characterId][u16 len][text][u16 type][u8 0]</c>.
	/// </summary>
	/// <remarks>
	///     The speaker is an id only; the client resolves the name from a <c>ChatSenderInfo</c> it has already been sent.
	///     <c>Type</c> is 0x0001 on every say sent here and is carried rather than fixed. The GameServer holds the
	///     positions, so it passes the in-range recipient ids down and this tier only frames and delivers.
	/// </remarks>
	public static class CharacterChatCodec
	{
		public const ushort Opcode = (ushort)ChatServerInterfaceOpcodes.CharacterChat;

		// The type word every captured say carries.
		public const ushort SayType = 0x0001;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(CharacterChat value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string text)
		{
			return Build(new CharacterChat(characterId, text));
		}

		// An NPC's ambient bark. The parent composes the child: `NpcChatterCodec` renders the remoteformat and this frames
		// it, so a caller holding the bark never spells the text layer.
		public static byte[] Build(uint npcHandle, NpcChatterBark bark)
		{
			return Build(new CharacterChat(npcHandle, NpcChatterCodec.Build(bark)));
		}

		public static CharacterChat Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharacterChatCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static CharacterChat Read(ref SpanReader r)
		{
			var characterId = r.ReadU32Be();
			var text = r.ReadStringU16Length();
			var type = r.ReadU16Be();
			r.ExpectU8(0, "the trailing byte");

			return new CharacterChat(characterId, text, type);
		}

		internal static void Write(FaolanStream s, CharacterChat v)
		{
			s.WriteUInt32(v.CharacterId).WriteString(v.Text).WriteUInt16(v.Type).WriteByte(0);
		}
	}

	/// <summary>The s2c CharacterChat body.</summary>
	public sealed record CharacterChat(uint CharacterId, string Text, ushort Type = CharacterChatCodec.SayType);
}

using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The server->client Knubot conversation records (#188, docs/re/iir-readers-conversation-misc.md). A FAMILY
	// codec (#530 rule 5): the four IIRs share one header grammar and are discriminated by the type word at offset
	// 0, so `Parse` returns the base `KnubotIIR` and `Build` dispatches on the concrete record.
	//
	// The Knubot carrier nests BOTH conversation endpoints: the player is the IIR carrier handle (`instance`), and
	// the KnubotBaseIIR_c header (FUN_009a7e00) carries the version + the NPC endpoint:
	//   [u32 sub][u32 0xC350][u32 playerInstance]
	//   [u8 env][u16 version=1][u32 0xC350][u32 npcHandle]            <- KnubotBaseIIR_c header
	//   [ type-specific body ]
	// Big-endian; golden-tested byte-exact in KnubotCodecTests. The carrier's [u32 len] belongs to the SENDER
	// (#530 rule 2) - ConversationService frames it.
	//
	// The c2s answer pick (KnubotAnswerIIR) is NOT part of this family: it carries no KnubotBase header and its
	// grammar is only partly decoded, so it has its own codec next door.
	public static class KnubotCodec
	{
		// The IIR types this codec constructs and parses (read by the coverage matrix).
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.KnubotAnswerListIIR, GameServer0X2000Opcodes.KnubotAppendTextIIR, GameServer0X2000Opcodes.KnubotCloseChatWindowIIR,
			GameServer0X2000Opcodes.KnubotOpenChatWindowIIR
		];

		private const ushort Version = 1;

		public static byte[] Build(KnubotIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static KnubotIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(KnubotCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static KnubotIIR Read(ref SpanReader r)
		{
			var sub = (GameServer0X2000Opcodes)r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the carrier entity class");
			var player = r.ReadU32Be();
			var env = r.ReadU8();
			r.ExpectU16Be(Version, "the KnubotBase version");
			r.ExpectU32Be(EntityClass.Character, "the KnubotBase entity class");
			var npc = r.ReadU32Be();

			switch (sub)
			{
				case GameServer0X2000Opcodes.KnubotOpenChatWindowIIR:
					return new KnubotOpenChatWindowIIR(player, npc, env, r.ReadU8());
				case GameServer0X2000Opcodes.KnubotAppendTextIIR:
					var field = r.ReadU32Be();
					var xml = r.ReadBytesU16Length().ToArray();

					return new KnubotAppendTextIIR(player, npc, env, field, xml, r.ReadU8());
				case GameServer0X2000Opcodes.KnubotAnswerListIIR:
					return new KnubotAnswerListIIR(player, npc, env, ReadStrings(ref r));
				case GameServer0X2000Opcodes.KnubotCloseChatWindowIIR:
					return new KnubotCloseChatWindowIIR(player, npc, env, ReadStrings(ref r));
				default:
					throw CodecParseException.Unexpected(nameof(KnubotCodec), 0, "a Knubot IIR sub-opcode", $"0x{(uint)sub:X8}");
			}
		}

		internal static void Write(FaolanStream s, KnubotIIR v)
		{
			switch (v)
			{
				case KnubotOpenChatWindowIIR open:
					Header(s, GameServer0X2000Opcodes.KnubotOpenChatWindowIIR, open).WriteByte(open.Value);

					break;
				case KnubotAppendTextIIR text:
					Header(s, GameServer0X2000Opcodes.KnubotAppendTextIIR, text).WriteUInt32(text.Field).WriteUInt16(text.Xml.Length).WriteArray(text.Xml).WriteByte(text.Trailing);

					break;
				case KnubotAnswerListIIR answers:
					WriteStrings(Header(s, GameServer0X2000Opcodes.KnubotAnswerListIIR, answers), answers.Strings);

					break;
				case KnubotCloseChatWindowIIR close:
					WriteStrings(Header(s, GameServer0X2000Opcodes.KnubotCloseChatWindowIIR, close), close.Strings);

					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(v), v, "not a Knubot record");
			}
		}

		private static FaolanStream Header(FaolanStream s, GameServer0X2000Opcodes sub, KnubotIIR v)
		{
			return s.WriteUInt32(sub)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.PlayerInstance)
				.WriteByte(v.Env)
				.WriteUInt16(Version)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.NpcHandle);
		}

		// KnubotAnswerListIIR_c (0xF164A4A5) + KnubotCloseChatWindowIIR_c (0xC4AF39EB) share one body (verified
		// 197/197 + 48/48): `[u32 count][ count x ([u16 len][remoteformat string]) ]`. The answer list is the
		// screen's option buttons; the close-window is the same shape with a single empty remoteformat (id="0"),
		// retail's "nothing (more) to say" reply. ⚠️ The count is a PLAIN u32, not a ListFrame word.
		private static List<byte[]> ReadStrings(ref SpanReader r)
		{
			var count = checked((int)r.ReadU32Be());
			var strings = new List<byte[]>(count);
			for (var i = 0; i < count; i++)
				strings.Add(r.ReadBytesU16Length().ToArray());

			return strings;
		}

		private static void WriteStrings(FaolanStream s, IReadOnlyList<byte[]> strings)
		{
			s.WriteUInt32(strings.Count);
			foreach (var str in strings)
				s.WriteUInt16(str.Length).WriteArray(str);
		}
	}

	public abstract record KnubotIIR(uint PlayerInstance, uint NpcHandle, byte Env);

	// KnubotAnswerListIIR_c (0xF164A4A5) - the screen's option buttons, each a `remoteformat` XML string.
	public sealed record KnubotAnswerListIIR(uint PlayerInstance, uint NpcHandle, byte Env, IReadOnlyList<byte[]> Strings) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotAppendTextIIR_c (0x4A3D899A) - an NPC dialogue line. The string is the `remoteformat` XML (the same
	// shape ChatText/Feedback use).
	public sealed record KnubotAppendTextIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint Field, byte[] Xml, byte Trailing) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotCloseChatWindowIIR_c (0xC4AF39EB) - the AnswerList body carrying retail's empty "nothing more to say".
	public sealed record KnubotCloseChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, IReadOnlyList<byte[]> Strings) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotOpenChatWindowIIR_c (0x045B0B0F) - opens the dialogue window. A single trailing byte.
	public sealed record KnubotOpenChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, byte Value) : KnubotIIR(PlayerInstance, NpcHandle, Env);
}

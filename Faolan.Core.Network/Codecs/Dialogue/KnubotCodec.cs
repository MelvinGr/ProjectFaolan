using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The Knubot conversation records (#188, docs/re/iir-readers-conversation-misc.md). A FAMILY codec (#530 rule
	// 5): the five IIRs share one header grammar and are discriminated by the type word at offset 0, so `Parse`
	// returns the base `KnubotIIR` and `Build` dispatches on the concrete record.
	//
	// The Knubot carrier nests BOTH conversation endpoints: the player is the IIR carrier handle (`instance`), and
	// the KnubotBaseIIR_c header (FUN_009a7e00) carries the version + the NPC endpoint:
	//   [u32 sub][u32 0xC350][u32 playerInstance]
	//   [u8 env][u16 version=1][u32 0xC350][u32 npcHandle]            <- KnubotBaseIIR_c header
	//   [ type-specific body ]
	// Big-endian; golden-tested byte-exact in KnubotCodecTests. The carrier's [u32 len] belongs to the SENDER
	// (#530 rule 2) - ConversationService frames it.
	//
	// ⚠️ The c2s answer pick belongs here TOO (#534). It had its own codec, on the stated grounds that it "carries
	// no KnubotBase header" and that everything ahead of its answer index was an undecoded opaque `Prefix`. It is
	// the SAME header: swept over the corpus, all 586 captured KnubotAnswer bodies are 31 bytes and every one reads
	// `[u32 0xC350][u32 player][u8 0x00][u16 1][u32 0xC350][u32 npc][u32 index]` - the prefix was a KnubotBase
	// header with `env` 0 all along, and the "observed shape [u32 0xC350][u32 instance]" in the old comment was the
	// first two words of it. The other two c2s Knubot messages (FinishTrade 6/6, CloseChatWindow 3/3) open with the
	// identical header, so the envelope is the family's, not one message's.
	public static class KnubotCodec
	{
		// The IIR types this codec constructs and parses (read by the coverage matrix).
		public static readonly IirTypes[] HandledTypes =
		[
			IirTypes.KnubotAnswerIIR, IirTypes.KnubotAnswerListIIR, IirTypes.KnubotAppendTextIIR,
			IirTypes.KnubotCloseChatWindowIIR, IirTypes.KnubotOpenChatWindowIIR
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
			var sub = (IirTypes)r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the carrier entity class");
			var player = r.ReadU32Be();
			var env = r.ReadU8();
			r.ExpectU16Be(Version, "the KnubotBase version");
			r.ExpectU32Be(EntityClass.Character, "the KnubotBase entity class");
			var npc = r.ReadU32Be();

			switch (sub)
			{
				case IirTypes.KnubotOpenChatWindowIIR:
				{
					return new KnubotOpenChatWindowIIR(player, npc, env, r.ReadU8());
				}

				case IirTypes.KnubotAppendTextIIR:
				{
					var textType = r.ReadU32Be();
					var xml = r.ReadBytesU16Length().ToArray();

					return new KnubotAppendTextIIR(player, npc, env, textType, xml, r.ReadU8());
				}

				case IirTypes.KnubotAnswerListIIR:
				{
					return new KnubotAnswerListIIR(player, npc, env, ReadStrings(ref r));
				}

				case IirTypes.KnubotCloseChatWindowIIR:
				{
					return new KnubotCloseChatWindowIIR(player, npc, env, r.ReadU32Be(), r.ReadBytesU16Length().ToArray());
				}

				case IirTypes.KnubotAnswerIIR:
				{
					return new KnubotAnswerIIR(player, npc, env, r.ReadU32Be());
				}

				default:
				{
					throw CodecParseException.Unexpected(nameof(KnubotCodec), 0, "a Knubot IIR sub-opcode", $"0x{(uint)sub:X8}");
				}
			}
		}

		internal static void Write(FaolanStream s, KnubotIIR v)
		{
			switch (v)
			{
				case KnubotOpenChatWindowIIR open:
				{
					Header(s, IirTypes.KnubotOpenChatWindowIIR, open).WriteByte(open.Value);

					break;
				}

				case KnubotAppendTextIIR text:
				{
					Header(s, IirTypes.KnubotAppendTextIIR, text).WriteUInt32(text.TextType).WriteUInt16(text.Xml.Length).WriteArray(text.Xml).WriteByte(text.Trailing);

					break;
				}

				case KnubotAnswerListIIR answers:
				{
					WriteStrings(Header(s, IirTypes.KnubotAnswerListIIR, answers), answers.Strings);

					break;
				}

				case KnubotCloseChatWindowIIR close:
				{
					Header(s, IirTypes.KnubotCloseChatWindowIIR, close).WriteUInt32(close.TextType).WriteUInt16(close.Text.Length).WriteArray(close.Text);

					break;
				}

				case KnubotAnswerIIR answer:
				{
					Header(s, IirTypes.KnubotAnswerIIR, answer).WriteUInt32(answer.Index);

					break;
				}

				default:
				{
					throw new ArgumentOutOfRangeException(nameof(v), v, "not a Knubot record");
				}
			}
		}

		private static FaolanStream Header(FaolanStream s, IirTypes sub, KnubotIIR v)
		{
			return s.WriteUInt32(sub)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.PlayerInstance)
				.WriteByte(v.Env)
				.WriteUInt16(Version)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.NpcHandle);
		}

		// KnubotAnswerListIIR_c (0xF164A4A5): `[u32 count][ count x ([u16 len][remoteformat string]) ]` - the screen's
		// option buttons. The client's reader (FUN_009A3A10) validates the count into 1..1000 by name
		// (`nAnswerCount`), so this really is the list it looks like. ⚠️ The count is a PLAIN u32, not a ListFrame word.
		//
		// ⚠️ KnubotCloseChatWindowIIR does NOT share this body (#809) - see KnubotCloseChatWindowIIR.
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
	// shape ChatText/Feedback use). `TextType` is the client's OWN name for the leading word: its WriteSubClass logs
	// "NPC %d speaking with Character %d: TextType %d, Text length = %d" (FUN_009A4EC0). It is 1 on 897 of the 907
	// captured lines and 6 on the other 10; what those select is undecoded [V0].
	public sealed record KnubotAppendTextIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint TextType, byte[] Xml, byte Trailing) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotCloseChatWindowIIR_c (0xC4AF39EB) - retail's "nothing (more) to say", closing the dialogue window.
	//
	// ⚠️ This is `[u32 TextType][u16 len][text]` - the SAME body as KnubotAppendText minus its trailing bool, NOT the
	// AnswerList's count-framed string list (#809). The two are indistinguishable on the 179 captured s2c records,
	// every one of which carries `[00000001][005F][<a 95-byte empty remoteformat>]` - identical bytes whether that
	// leading 1 is a list COUNT with one string after it or a TextType with one string after it. The client settles
	// it: `KnubotCloseChatWindowIIR_c::ReadSubClass` (FUN_009A58D0) reads one u32 into `this+0x34` and then ONE
	// std::string into `this+0x38`, with no loop, and its WriteSubClass logs that string as "Text length". Reading it
	// as a list is what refused the 7 captured c2s records, whose `[00000000][0000]` is a TextType of 0 and an EMPTY
	// text - and, under the list reading, a zero count with two bytes left over.
	public sealed record KnubotCloseChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint TextType, byte[] Text) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotOpenChatWindowIIR_c (0x045B0B0F) - opens the dialogue window. A single trailing byte.
	public sealed record KnubotOpenChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, byte Value) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	// KnubotAnswerIIR_c (0x4D7D9123) - the CLIENT's answer pick: the chosen option's index, which selects the
	// branch (ConversationService.AdvanceConversation routes it). The only c2s record in the family, and the only
	// one whose whole body past the header is a single field. `Env` is 0 on all 586 captured picks and `Index`
	// runs 0..6.
	public sealed record KnubotAnswerIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint Index) : KnubotIIR(PlayerInstance, NpcHandle, Env);
}

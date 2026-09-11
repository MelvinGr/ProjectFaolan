using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     The Knubot conversation records (docs/re/iir-readers-conversation-misc.md). A family codec: the five IIRs share
	///     one header grammar and are discriminated by the type word at offset 0, so <c>Parse</c> returns the base
	///     <c>KnubotIIR</c> and <c>Build</c> dispatches on the concrete record.
	/// </summary>
	/// <remarks>
	///     The Knubot carrier nests both conversation endpoints: the player is the IIR carrier handle, and the
	///     <c>KnubotBaseIIR_c</c> header (FUN_009a7e00) carries the version plus the NPC endpoint.
	///     <code>
	///     [u32 sub][u32 entityClass][u32 playerInstance]
	///     [u8 env][u16 version=1][u32 entityClass][u32 npcHandle]      &lt;- KnubotBaseIIR_c header
	///     [ type-specific body ]
	///     </code>
	///     Big-endian, golden-tested byte-exact in <c>KnubotCodecTests</c>. The carrier's u32 length belongs to the
	///     sender; <c>ConversationService</c> frames it.
	///     <para>
	///         The c2s answer pick belongs here too: it carries the same header. Swept over the corpus, all 586 captured
	///         KnubotAnswer bodies are 31 bytes and every one reads
	///         <c>[u32 entityClass][u32 player][u8 0x00][u16 1][u32 entityClass][u32 npc][u32 index]</c>. The other two
	///         c2s Knubot messages (FinishTrade 6/6, CloseChatWindow 3/3) open with the identical header, so the envelope
	///         is the family's rather than one message's.
	///     </para>
	/// </remarks>
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

		// KnubotAnswerListIIR_c: `[u32 count][ count x ([u16 len][remoteformat string]) ]` - the screen's option buttons.
		// The client's reader (FUN_009A3A10) validates the count into 1..1000 by name (`nAnswerCount`), so this really is
		// the list it looks like. The count is a plain u32, not a list frame.
		//
		// KnubotCloseChatWindowIIR does not share this body - see its own record.
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

	/// <summary>
	///     <c>KnubotAnswerListIIR_c</c> - the screen's option buttons, each a <c>remoteformat</c> XML string.
	/// </summary>
	public sealed record KnubotAnswerListIIR(uint PlayerInstance, uint NpcHandle, byte Env, IReadOnlyList<byte[]> Strings) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	/// <summary>
	///     <c>KnubotAppendTextIIR_c</c> - an NPC dialogue line. The string is the <c>remoteformat</c> XML, the same shape
	///     ChatText and Feedback use.
	/// </summary>
	/// <remarks>
	///     <c>TextType</c> is the client's own name for the leading word: its <c>WriteSubClass</c> logs
	///     "NPC %d speaking with Character %d: TextType %d, Text length = %d" (FUN_009A4EC0). It is 1 on 897 of the 907
	///     captured lines and 6 on the other 10; what those select is undecoded. [V0]
	/// </remarks>
	public sealed record KnubotAppendTextIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint TextType, byte[] Xml, byte Trailing) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	/// <summary>
	///     <c>KnubotCloseChatWindowIIR_c</c> - retail's "nothing (more) to say", closing the dialogue window.
	/// </summary>
	/// <remarks>
	///     The body is <c>[u32 TextType][u16 len][text]</c>: the same body as KnubotAppendText minus its trailing bool,
	///     not the AnswerList's count-framed string list. The two are indistinguishable on the 179 captured s2c records,
	///     every one of which carries a leading 1 followed by one string. The client settles it:
	///     <c>KnubotCloseChatWindowIIR_c::ReadSubClass</c> (FUN_009A58D0) reads one u32 into <c>this+0x34</c> and then one
	///     std::string into <c>this+0x38</c>, with no loop, and its <c>WriteSubClass</c> logs that string as "Text
	///     length". Reading it as a list also refuses the 7 captured c2s records, whose leading 0 and empty text would be
	///     a zero count with two bytes left over.
	/// </remarks>
	public sealed record KnubotCloseChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint TextType, byte[] Text) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	/// <summary><c>KnubotOpenChatWindowIIR_c</c> - opens the dialogue window. A single trailing byte.</summary>
	public sealed record KnubotOpenChatWindowIIR(uint PlayerInstance, uint NpcHandle, byte Env, byte Value) : KnubotIIR(PlayerInstance, NpcHandle, Env);

	/// <summary>
	///     <c>KnubotAnswerIIR_c</c> - the client's answer pick: the chosen option's index, which selects the branch
	///     (<c>ConversationService.AdvanceConversation</c> routes it).
	/// </summary>
	/// <remarks>
	///     The only c2s record in the family, and the only one whose whole body past the header is a single field.
	///     <c>Env</c> is 0 on all 586 captured picks and <c>Index</c> runs 0..6.
	/// </remarks>
	public sealed record KnubotAnswerIIR(uint PlayerInstance, uint NpcHandle, byte Env, uint Index) : KnubotIIR(PlayerInstance, NpcHandle, Env);
}

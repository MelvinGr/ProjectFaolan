using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Quest
{
	/// <summary>
	///     QuestIIR - adds or updates one quest in the player's journal, 29 bytes:
	///     <c>[subop][class][playerHandle][u32 0][u8 0x01][u32 state][u32 questId][u32 0]</c>.
	/// </summary>
	/// <remarks>
	///     On completion retail sends it twice for the quest - state 2 (active/update) then state 4 (completed); see the
	///     Stihetfv compeltefirstquest capture, quest 13479 at idx 858 and 860.
	///     <para>The u32 length prefix belongs to the framing sender, not to this body.</para>
	/// </remarks>
	public static class QuestCodec
	{
		public const uint Opcode = (uint)IirTypes.QuestIIR;

		/// <summary>
		///     Quest state codes, from the capture.
		/// </summary>
		/// <remarks>
		///     <see cref="StateCompleted" /> only means completed going down. The client sends this same record up to abandon
		///     a quest - QuestIIR_c is its DeleteQuest method - and over the whole corpus the c2s direction carries state 4
		///     and nothing else: 3 c2s records against 50 s2c completions and 47 s2c updates. The code is the message's slot
		///     rather than its meaning, so a reader that ignores direction turns a deletion into its opposite;
		///     GameServerListener.Inbound's QuestIIR case makes that distinction.
		/// </remarks>
		public const uint StateActive = 2;
		public const uint StateCompleted = 4;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(QuestIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint playerHandle, uint questId, uint state)
		{
			return Build(new QuestIIR(playerHandle, questId, state));
		}

		public static QuestIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(QuestCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static QuestIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "QuestIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU32Be(0, "reserved word before the marker");
			r.ExpectU8(0x01, "record marker");
			var state = r.ReadU32Be();
			var questId = r.ReadU32Be();
			r.ExpectU32Be(0, "trailing reserved word");
			return new QuestIIR(handle, questId, state);
		}

		internal static void Write(FaolanStream s, QuestIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.PlayerHandle).WriteUInt32(0u).WriteByte((byte)0x01).WriteUInt32(v.State).WriteUInt32(v.QuestId).WriteUInt32(0u);
		}
	}

	/// <summary>The QuestIIR body.</summary>
	public sealed record QuestIIR(uint PlayerHandle, uint QuestId, uint State);
}

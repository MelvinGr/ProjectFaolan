using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     GameActionListIIR - the per-dynel action list, one of the three movement and AI records. Server to client it
	///     pushes the action list bound to a dynel: emote loops, the creation-step template-choice list, and numeric
	///     position and timer action records.
	/// </summary>
	/// <remarks>
	///     This is the simulation layer's output - the world model decides which action a dynel runs - so the action tail
	///     is per-action-type and not fully decoded. The codec models the fixed header, decoded and validated over all 570
	///     captured bodies (tools/capture/decode_gameactionlist.py), and carries the action tail verbatim, round-tripping
	///     byte-exact (GameActionListCodecTests).
	///     <para>
	///         The wire body, the inner IIR record after the u32 total-length wrapper, is big-endian throughout:
	///         <code>
	///         [subop][class][frameHandle]
	///         [u8 0][ListFrame(1)][u32 dynelHandle][u32 field3][u32 4][u64 1][actionTail...]
	///         </code>
	///         frameHandle is the receiving player's instance and dynelHandle the entity the action list is about; the 4
	///         and 1 words are constant across all 570 captures. Those five words after the carrier flag are also the first
	///         five of the GameActionData element (typeId, field, version 4, subCount 0, 0x2d5 = 1), which is why the tail
	///         starts at <see cref="GameActionDataCodec.PrefixThrough0x2d5" />.
	///     </para>
	///     <para>
	///         <c>field3</c> is the element's <c>field</c>, retail's own unique-instance counter - see
	///         <see cref="GameActionInstanceId" />. Zero means the element was not constructed by that constructor, which
	///         is what a template-loaded action carries: 1,558 of the 1,745 captured elements are that, and the 187 that are
	///         not form 40 per-(capture, server port) series, monotonic in every one. Build a constructed action with
	///         <c>GameActionInstanceId.Next()</c>, never with 0.
	///     </para>
	///     <para>
	///         The word before the dynel handle is a count-framed list word for one GameActionData element, not a constant.
	///         The captures cannot show that - every captured frame carries the one-element word across 28 body lengths,
	///         which fits both readings - but the client settles it. Its list reader (FUN_00870500, reached from
	///         GameActionListIIR_t::ReadSubClass, FUN_008679f0) validates the word as a positive multiple of the frame
	///         unit, logs "Inconsistent spell list.." and aborts the stream otherwise, then reads that many blobs, logging
	///         "Streaming in spell list with N elements" and refusing a thousand or more. So a wrong word here is not
	///         cosmetic, and the count decides how many blobs the client reads.
	///     </para>
	/// </remarks>
	public static class GameActionListCodec
	{
		/// <summary>
		///     How many GameActionData elements this codec emits and expects. It is one, because the body carries exactly one
		///     action tail blob; a multi-action body would need the tail split per element first.
		/// </summary>
		public const int ActionCount = 1;

		public const uint Opcode = (uint)IirTypes.GameActionListIIR;

		private const uint ConstFour = 4;
		private const ulong ConstOne = 1;

		/// <summary>
		///     The ReadSubClass trailer: GameActionListIIR_t::ReadSubClass (FUN_008679f0) reads the element list and then a
		///     [u8 flag] (+0x3c) and a [u32] (+0x40). Every captured body ends in those five zero bytes, so a constructed
		///     body has to carry them or the client reads past the record.
		/// </summary>
		public static readonly byte[] ReadSubClassTrailer = [0x00, 0x00, 0x00, 0x00, 0x00];

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(GameActionListIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     Wraps one GameActionData element as a complete body delivered to <paramref name="frameHandle" />. The list
		///     header already writes the element's first five words, so only the remainder rides as the action tail; building
		///     the whole element first and slicing keeps the two construction paths in agreement.
		/// </summary>
		/// <remarks>
		///     <paramref name="trailer" /> appends the <see cref="ReadSubClassTrailer" /> every captured body ends with. It
		///     defaults to false for the one construct-only caller that is never sent; anything going on the wire wants it
		///     true.
		/// </remarks>
		public static byte[] Build(uint frameHandle, GameActionData action, bool trailer = false)
		{
			var element = GameActionDataCodec.Build(action);
			var tail = element[GameActionDataCodec.PrefixThrough0x2d5..];
			if (trailer)
				tail = [.. tail, .. ReadSubClassTrailer];
			return Build(new GameActionListIIR(frameHandle, action.TypeId, action.Field, tail));
		}

		public static GameActionListIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GameActionListCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static GameActionListIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "GameActionListIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var frameHandle = r.ReadU32Be();
			r.ExpectU8(0, "carrier flag");

			// Validated as a count - the client aborts the stream on a non-multiple with "Inconsistent spell list.." -
			// rather than compared to the one-element word, which would reject any multi-action body as malformed.
			var actionCount = r.ReadListCount();
			if (actionCount != ActionCount)
				throw CodecParseException.Unexpected(nameof(GameActionListCodec), r.Position - 4, $"{ActionCount} action (the tail is verbatim)", $"{actionCount}");

			var dynelHandle = r.ReadU32Be();
			var field3 = r.ReadU32Be();
			r.ExpectU32Be(ConstFour, "GameActionData version");
			r.ExpectU64Be(ConstOne, "subCount + fixed int 0x2d5");
			return new GameActionListIIR(frameHandle, dynelHandle, field3, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, GameActionListIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.FrameHandle)
				.WriteByte(0x00)
				.WriteUInt32(ListFrame.ForCount(ActionCount)) // one GameActionData element - ActionTail is that one blob
				.WriteUInt32(v.DynelHandle)
				.WriteUInt32(v.Field3)
				.WriteUInt32(ConstFour)
				.WriteUInt64(ConstOne)
				.WriteArray(v.ActionTail);
		}
	}

	/// <summary>
	///     The GameActionListIIR body. <c>DynelHandle</c> and <c>Field3</c> are also the element's type id and field, and
	///     <c>ActionTail</c> is the rest of that element, from <see cref="GameActionDataCodec.PrefixThrough0x2d5" />
	///     onward, still opaque per action type.
	/// </summary>
	public sealed record GameActionListIIR(uint FrameHandle, uint DynelHandle, uint Field3, byte[] ActionTail)
	{
		/// <summary>
		///     Whether the tail is a travel action. The tail is per-action-type and the codec carries it verbatim by design,
		///     so decoding it stays the caller's step (<see cref="TravelActionCodec" />.Parse), but the mapping from the type
		///     word to the codec that reads it belongs beside the word that selects it rather than copied into every caller.
		///     It sits on the value rather than on the codec because a codec's surface is Build and Parse and nothing else.
		/// </summary>
		public bool IsTravelAction => DynelHandle == TravelActionCodec.TypeId;

	}
}

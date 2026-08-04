using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// GameActionListIIR_t (0xF508F4C1) - the per-dynel ACTION list (one of the three movement/AI records, #190).
	// Server->client it pushes the action list bound to a dynel (NPC/object): emote loops ("idle2_emote"), the
	// creation-step template-choice list, and numeric position/timer action records. This is the SIMULATION layer's
	// output (the world model decides which action a dynel runs), so the action TAIL is per-action-type and not yet
	// fully decoded; this codec models the FIXED header (decoded + validated 570/570, tools/capture/decode_gameactionlist.py)
	// and carries the action tail VERBATIM, round-tripping byte-exact (GameActionListCodecTests). That replaces the
	// fully-verbatim emit with a header CONSTRUCTED for the live frame/dynel handle + the (still-opaque) action bytes -
	// the construct-don't-replay step, mirroring how QuestcRegion types a header and carries unknowns verbatim.
	//
	// Wire body (the inner 0x2000 IIR record, after the u32 totalLen wrapper), all big-endian:
	//   [subop 0xF508F4C1][0x0000C350][frameHandle] then the GameActionList payload:
	//   [u8 0][u32 0x000007E2][u32 dynelHandle][u32 field3][u32 4][u64 1][actionTail...]
	// frameHandle = the receiving player's instance; dynelHandle = the entity the action list is about; field3 is
	// usually 0 (a small id in some captures, undecoded); the 0x7E2 / 4 / 1 words are constant across all 570 captures.
	//
	// ⚠️ Those five words are ALSO the first five of the GameActionData element (typeId / field / version=4 /
	// subCount=0 / 0x2d5=1) - which is why the "tail" starts at GameActionDataCodec.PrefixThrough0x2d5.
	//
	// The 0x7E2 is NOT a constant: it is the count-framed list word for ONE GameActionData element (ListFrame). The
	// captures cannot show this - all 1305 GameActionListIIR frames carry 0x7E2 across 28 body lengths, which fits
	// "a constant" and "a 1-element count with variable-length blobs" equally well. The CLIENT settles it
	// (FUN_00870500, the GameActionData list reader reached from GameActionListIIR_t::ReadSubClass FUN_008679f0):
	//
	//     iVar3 = ReadU32BE();
	//     if ((iVar3 == (iVar3 / 0x3f1) * 0x3f1) && (0 < iVar3)) {   // must be a positive multiple of 0x3F1
	//         iVar3 = iVar3 / 0x3f1 + -1;                            // count = word/0x3F1 - 1
	//         ... "Streaming in spell list with ", iVar3, " elements"
	//         if (iVar3 < 1000) { do { <read a GameActionData blob> } while (i < count); }
	//         else  ... "Way too many entries.."
	//     } else    ... "Inconsistent spell list.."
	//
	// So a wrong word here is not cosmetic: a non-multiple aborts the stream ("Inconsistent spell list.."), and the
	// count decides how many blobs the client reads. We always send exactly one action, hence ForCount(1) == 0x7E2 -
	// the same bytes we have always sent, now for the right reason and derived rather than asserted.
	public static class GameActionListCodec
	{
		// How many GameActionData elements this codec emits/expects. The wire word is ListFrame.ForCount(ActionCount)
		// - the client divides it by 0x3F1 and reads that many blobs (see the class comment). One, because the body
		// carries exactly one `ActionTail` blob; a multi-action body would need the tail split per element first.
		public const int ActionCount = 1;

		public const uint Opcode = (uint)IirTypes.GameActionListIIR;

		private const uint ConstFour = 4;
		private const ulong ConstOne = 1;

		// The ReadSubClass trailer: GameActionListIIR_t::ReadSubClass (FUN_008679f0) reads the element list and THEN a
		// [u8 flag] (+0x3c) and a [u32] (+0x40). Every captured body ends in those five zero bytes, so a CONSTRUCTED
		// body has to carry them or the client reads past the record.
		public static readonly byte[] ReadSubClassTrailer = [0x00, 0x00, 0x00, 0x00, 0x00];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GameActionListIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// Wraps one GameActionData element as a complete body delivered to `frameHandle`. The list header already
		// writes the element's first five words, so only the remainder rides as the action tail - building the whole
		// element first and slicing keeps the two construction paths in agreement.
		// `trailer` appends the ReadSubClass [u8][u32] every captured body ends with. It defaults to FALSE only because
		// the pre-existing recast builder was written without it and is construct-only (never sent); anything actually
		// going on the wire wants it true.
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

			// Validated AS A COUNT (the client aborts the stream on a non-multiple - "Inconsistent spell list.."), not
			// compared to 0x7E2: this used to demand `head == 0x7E2` exactly, which would have rejected any
			// multi-action body as malformed. We only BUILD one element, but rejecting more on parse was asserting a
			// constant that isn't one.
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
				.WriteUInt32(ListFrame.ForCount(ActionCount)) // one GameActionData element - `ActionTail` is that one blob
				.WriteUInt32(v.DynelHandle)
				.WriteUInt32(v.Field3)
				.WriteUInt32(ConstFour)
				.WriteUInt64(ConstOne)
				.WriteArray(v.ActionTail);
		}
	}

	// GameActionListIIR_t (0xF508F4C1). DynelHandle/Field3 are also the element's typeId/field; ActionTail is the rest
	// of that element (from GameActionDataCodec.PrefixThrough0x2d5), still opaque per action type.
	public sealed record GameActionListIIR(uint FrameHandle, uint DynelHandle, uint Field3, byte[] ActionTail)
	{
		// Whether the tail is one we can TYPE. The tail is per-action-type and the codec carries it verbatim by design,
		// so decoding it stays the caller's step (TravelActionCodec.Parse) - but the mapping from the type word to the
		// codec that reads it belongs beside the word that selects it, not copied into every caller. It sits on the
		// VALUE rather than on the codec because a codec's surface is Build and Parse and nothing else (#530 rule 1).
		// 0xD00D is the only tail decoded so far: the move-the-player action behind local travel (#514).
		public bool IsTravelAction => DynelHandle == TravelActionCodec.TypeId;
	}
}

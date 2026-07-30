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
		// - the client divides it by 0x3F1 and reads that many blobs (see the class comment). One, because BuildBody
		// carries exactly one `actionTail` blob; a multi-action body would need the tail split per element first.
		public const int ActionCount = 1;

		public const uint ConstFour = 4;

		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.GameActionListIIR;
		public const ulong ConstOne = 1;

		// Builds the GameActionList body for `dynelHandle`, delivered to `frameHandle`, carrying `actionTail` verbatim.
		public static byte[] BuildBody(uint frameHandle, uint dynelHandle, uint field3, byte[] actionTail)
		{
			ArgumentNullException.ThrowIfNull(actionTail);
			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.GameActionListIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(frameHandle)
				.WriteByte(0x00)
				.WriteUInt32(ListFrame.ForCount(1)) // one GameActionData element - `actionTail` is that one blob
				.WriteUInt32(dynelHandle)
				.WriteUInt32(field3)
				.WriteUInt32(ConstFour)
				.WriteUInt64(ConstOne)
				.WriteArray(actionTail)
				.ToArray();
		}

		// Parses a captured/built body back into its header fields + the verbatim action tail (the inverse of BuildBody).
		// The list word is validated AS A COUNT (the client aborts the stream on a non-multiple - "Inconsistent spell
		// list.."), not compared to 0x7E2: this used to demand `head == 0x7E2` exactly, which would have rejected any
		// multi-action body as malformed. We only BUILD one element, but rejecting more on parse was asserting a
		// constant that isn't one.
		public static Parsed Parse(byte[] body)
		{
			ArgumentNullException.ThrowIfNull(body);
			var r = new FaolanStream(body);

			var subop = r.ReadUInt32();
			if (subop != (uint)GameServer0X2000Opcodes.GameActionListIIR)
				throw new InvalidOperationException($"GameActionList: subop 0x{subop:X} != 0x{(uint)GameServer0X2000Opcodes.GameActionListIIR:X}");

			r.ReadUInt32(); // EntityClass.Character
			var frameHandle = r.ReadUInt32();

			var flag = r.ReadByte();
			if (flag != 0)
				throw new InvalidOperationException($"GameActionList: flag 0x{flag:X} != 0");

			var head = r.ReadUInt32();
			if (!ListFrame.TryCountOf(head, out var actionCount))
				throw new InvalidOperationException($"GameActionList: head 0x{head:X} is not a count-framed list word");
			if (actionCount != ActionCount)
				throw new InvalidOperationException($"GameActionList: {actionCount} actions; only {ActionCount} is modelled (the tail is verbatim)");

			var dynelHandle = r.ReadUInt32();
			var field3 = r.ReadUInt32();
			var four = r.ReadUInt32();
			var one = r.ReadUInt64();
			if (four != ConstFour || one != ConstOne)
				throw new InvalidOperationException($"GameActionList: constants four=0x{four:X}/one=0x{one:X} != 4/1");

			var tail = r.ReadArray(r.Length - r.Position);
			return new Parsed(frameHandle, dynelHandle, field3, tail);
		}

		public sealed record Parsed(uint FrameHandle, uint DynelHandle, uint Field3, byte[] ActionTail);
	}
}

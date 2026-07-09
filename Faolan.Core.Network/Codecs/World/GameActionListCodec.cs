using System;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// GameActionListIIR_t (0xF508F4C1) - the per-dynel ACTION list (one of the three movement/AI records, #190).
	// Server->client it pushes the action list bound to a dynel (NPC/object): emote loops ("idle2_emote"), the
	// creation-step template-choice list, and numeric position/timer action records. This is the SIMULATION layer's
	// output (the world model decides which action a dynel runs), so the action TAIL is per-action-type and not yet
	// fully decoded; this codec models the FIXED header (decoded + validated 570/570, Captures/scripts/decode_gameactionlist.py)
	// and carries the action tail VERBATIM, round-tripping byte-exact (GameActionListCodecTests). That replaces the
	// fully-verbatim emit with a header CONSTRUCTED for the live frame/dynel handle + the (still-opaque) action bytes -
	// the construct-don't-replay step, mirroring how QuestcRegion types a header and carries unknowns verbatim.
	//
	// Wire body (the inner 0x2000 IIR record, after the u32 totalLen wrapper), all big-endian:
	//   [subop 0xF508F4C1][0x0000C350][frameHandle] then the GameActionList payload:
	//   [u8 0][u32 0x000007E2][u32 dynelHandle][u32 field3][u32 4][u64 1][actionTail...]
	// frameHandle = the receiving player's instance; dynelHandle = the entity the action list is about; field3 is
	// usually 0 (a small id in some captures, undecoded); the 0x7E2 / 4 / 1 words are constant across all 570 captures.
	public static class GameActionListCodec
	{
		public const uint ConstFour = 4;
		public const uint Head = 0x000007E2;
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
				.WriteUInt32(Head)
				.WriteUInt32(dynelHandle)
				.WriteUInt32(field3)
				.WriteUInt32(ConstFour)
				.WriteUInt64(ConstOne)
				.WriteArray(actionTail)
				.ToArray();
		}

		// Parses a captured/built body back into its header fields + the verbatim action tail (the inverse of BuildBody;
		// validates the constant 0x7E2 / 4 / 1 words so a malformed body is rejected rather than mis-framed).
		public static Parsed Parse(byte[] body)
		{
			ArgumentNullException.ThrowIfNull(body);
			var r = new FaolanStream(body);

			var subop = r.ReadUInt32();
			if (subop != (uint)GameServer0X2000Opcodes.GameActionListIIR)
				throw new InvalidOperationException($"GameActionList: subop 0x{subop:X} != 0x{(uint)GameServer0X2000Opcodes.GameActionListIIR:X}");

			r.ReadUInt32(); // 0xC350 type word
			var frameHandle = r.ReadUInt32();

			var flag = r.ReadByte();
			if (flag != 0)
				throw new InvalidOperationException($"GameActionList: flag 0x{flag:X} != 0");

			var head = r.ReadUInt32();
			if (head != Head)
				throw new InvalidOperationException($"GameActionList: head 0x{head:X} != 0x{Head:X}");

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

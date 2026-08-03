using System;

namespace Faolan.Core.Network.Constants
{
	// The RPC header's trailing `headerData` — ONE grammar across all 17 interfaces (Gitea #319/#320).
	//
	// It is a protobuf VARINT of the method's registration constant with its low 7 bits dropped, and it is OMITTED
	// when that value is zero (a zero varint is not written, as everywhere else in protobuf):
	//
	//     headerData = varint(registrationConstant >> 7),  absent when the result is 0
	//
	// Which gives the field two very different-looking faces, and that is why it was written up as four unrelated
	// per-opcode cases:
	//
	//   - PlayerAgent / PlayerInterface register each method with a 32-bit HASH, so `>> 7` is a 25-bit number and the
	//     field is 3 or 4 bytes. `PlayerAgentMethodIds` owns those (#320).
	//   - Every OTHER interface registers by plain INDEX (0, 1, 2, …) and the opcode is `0x2000 + index`. So `>> 7` is
	//     0 for methods 0-127 — no header data at all — and exactly 1 for methods 128-255, which is the single byte
	//     `0x01` that used to be called the "latency marker".
	//
	// ⚠️ **`0x01` is not a marker, a flag or a version.** Measured over the whole capture corpus: 402,111 of 402,113
	// index-registered packets carry exactly `varint(index >> 7)`, and the two that do not are mis-framed packets on
	// `GameInterface` at opcodes outside its 16-method range. Three facts kill every per-packet reading:
	//   - it is ALWAYS exactly `0x01` (12,056 sightings, never `0x00`, never `0x02`);
	//   - it is on 100% of the packets of any method that has it (5,917/5,917 pings, 5,689/5,689 pongs), so it is a
	//     property of the METHOD, not of the packet;
	//   - the lowest opcode carrying it is `0x2080` — index 128 exactly, the boundary the rule predicts.
	// It also appears on ~24 unrelated opcodes across four interfaces, which no per-method semantic explains and
	// "index >= 128" does.
	public static class RpcHeaderData
	{
		// The base every index-registered interface numbers its first method from.
		public const ushort MethodIndexBase = 0x2000;

		// The header data for an opcode on an INDEX-registered interface (i.e. anything but PlayerAgent /
		// PlayerInterface, whose ids come from PlayerAgentMethodIds). Null when the method's index is below 128 and
		// the field is therefore not written at all.
		public static byte[]? ForOpcode(ushort opcode)
		{
			if (opcode < MethodIndexBase)
				return null;

			var value = (uint)(opcode - MethodIndexBase) >> 7;
			return value == 0 ? null : PlayerAgentMethodIds.Varint(value);
		}

		// The same, for a caller that already holds the enum member.
		public static byte[]? ForOpcode<T>(T opcode) where T : struct, Enum
		{
			return ForOpcode(Convert.ToUInt16(opcode));
		}
	}
}

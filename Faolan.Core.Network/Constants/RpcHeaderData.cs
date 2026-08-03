using System;

namespace Faolan.Core.Network.Constants
{
	// The RPC header's trailing `headerData` — ONE grammar across all 17 interfaces (Gitea #319/#320/#609).
	//
	// It is a protobuf VARINT of the method's registration constant with its low 7 bits dropped, and it is OMITTED
	// when that value is zero (a zero varint is not written, as everywhere else in protobuf):
	//
	//     headerData = varint(registrationConstant >> 7),  absent when the result is 0
	//
	// ⚠️ **That `>> 7` is an artifact of where we cut the header, not something the client does.** `headerData` is not
	// a field at all: the u16 `Opcode` before it is protobuf tag `0x20` (field 4, varint) plus the varint's FIRST
	// group, and `headerData` is the remaining groups. Reassembled, the field is simply the method's registration
	// constant, whole. Splitting it this way is byte-identical and matches the shape the opcode enums and the
	// dissector are written against, so it stays for now — folding it back into one varint field is Gitea #610.
	// The reading is what explains every face this field has, each of which used to be written up as its own case:
	//
	//   - PlayerAgent / PlayerInterface register each method with a 32-bit HASH, so the varint is 5 groups and the
	//     tail is 3 or 4 bytes. `PlayerAgentMethodIds` owns those (#320).
	//   - Every OTHER interface registers by plain INDEX (0, 1, 2, …), so the varint IS the index: one group for
	//     methods 0-127 (no tail at all) and two for 128-255, whose second group is the single byte `0x01` that used
	//     to be called the "latency marker".
	//
	// ⚠️ **`0x01` is not a marker, a flag or a version** — it is a varint's second group. Measured over the whole
	// capture corpus: 404,208 of 404,210 field-4 frames carry a well-formed varint whose first group is the opcode's
	// low byte, the two misses being mis-framed packets on `GameInterface` at opcodes outside its 16-method range.
	// Three facts kill every per-packet reading, and all three fall out of the varint:
	//   - it is ALWAYS exactly `0x01` (12,056 sightings, never `0x00`, never `0x02`);
	//   - it is on 100% of the packets of any method that has it (5,917/5,917 pings, 5,689/5,689 pongs), so it is a
	//     property of the METHOD, not of the packet;
	//   - the lowest opcode carrying it is `0x2080` — where a constant first needs a second varint group.
	//
	// ⚠️ The two failure modes are NOT symmetric, which is why a spurious byte hid for so long: an EXTRA tail byte is
	// harmless (the varint ends at the first group without a continuation bit, and the stray byte is inside the
	// header's own length prefix, so the body is untouched), while a MISSING one truncates a varint mid-continuation.
	public static class RpcHeaderData
	{
		// The base every index-registered interface numbers its first method from.
		public const ushort MethodIndexBase = 0x2000;

		// The two receiver classes that register their methods by 32-bit HASH instead of by index, and so take their
		// header data from PlayerAgentMethodIds rather than from the opcode. Everything else - all 15 other classes -
		// is index-registered, so the DEFAULT is the index rule and this set is the exception list.
		//
		// Measured over the whole capture corpus (120 dumps, 419k RPC frames, `HeaderExtraHex` grouped by
		// (ReceiverClass, Opcode)): every frame on an index-registered class carries exactly `varint(index >> 7)`
		// except the two mis-framed GameInterface packets already noted above, and every frame on these two classes
		// carries `varint(registrationConstant >> 7)` for a method the client's own registration tables name.
		private static readonly uint[] HashRegisteredReceivers = [Endpoints.PlayerAgentType, Endpoints.PlayerInterfaceType];

		// The header data for a message, derived from the pair that fully determines it: the receiver's object CLASS
		// (which picks the interface, and so the registration scheme) and the 16-bit opcode (which picks the method).
		// Null when the method carries none - which is most of them. This is what Rpc computes for itself, so no
		// caller supplies the field.
		public static byte[]? For(uint receiverType, ushort opcode)
		{
			return Array.IndexOf(HashRegisteredReceivers, receiverType) >= 0
				? PlayerAgentMethodIds.HeaderId(opcode)
				: ForOpcode(opcode);
		}

		// The method a header names: the INVERSE of the (Opcode, headerData) split this class maintains, reassembling
		// the protobuf varint the two halves really are. The opcode's low byte is the varint's first group and the
		// header data is the rest, so the result is the method's registration constant - the index itself on the 15
		// index-registered classes, and the 32-bit constant on PlayerAgent/PlayerInterface.
		//
		// ⚠️ This, not the opcode, is what identifies a method on the hashed pair. `0x2080 | (constant & 0x7F)` means
		// two methods that share their low 7 bits share an opcode - `0x20EC` and `0x20D6` each name two, all four in
		// the corpus - so an inbound dispatch keyed on the opcode alone runs the wrong handler (#608).
		public static uint MethodConstant(ushort opcode, ReadOnlySpan<byte> headerData)
		{
			if (opcode < MethodIndexBase)
				throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Not a method-carrying opcode: the field-4 tag is 0x20, so the high byte must be 0x20.");

			var value = (uint)(opcode & 0x7F);
			for (var i = 0; i < headerData.Length; i++)
				value |= (uint)(headerData[i] & 0x7F) << (7 * (i + 1));

			return value;
		}

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

using System;

namespace Faolan.Core.Network.Protocol
{
	// The two bytes you read off a hex dump for an RpcHeader's method field: the field's tag `0x20`, then the varint's
	// FIRST group. It is what the Wireshark plugin labels "Opcode", what `tshark_dump.sh` records as `"Opcode"` in a
	// `.tshark.json`, and what the codebase called an opcode for years.
	//
	// ⚠️ **It is a RENDERING of `Rpc.Method`, never an identity.** #615 renumbered the method enums onto
	// `method_instance` itself and deleted `Rpc.Opcode`, because this view is not injective: it keeps 7 bits of the
	// constant plus a continuation flag, so on the hash-registered PlayerAgent/PlayerInterface pair two methods share
	// it - `0x20EC` is BOTH `ReceivedCharacterSettings` and `AuthenticateLoggedIn` (#608/#613). On the fifteen
	// index-registered classes it happens to be exact (an index <= 0xFF is one varint group, or two whose first group
	// equals it), but "exact on most classes" is precisely the property that let a lossy value be used as a key.
	//
	// So this exists for the two jobs a rendering is for, and no others:
	//   * naming a frame you are looking at in a capture or a dump (docs/protocol/gen/opcodes.md, the timeline report);
	//   * meeting a value that was RECORDED in this space by something outside our code - the tshark dumps'
	//     `CapturedPacket.WireOpcode`, and the u16 that ends a captured rpcHeader.
	// Anything that DISPATCHES, keys a table, or declares a codec's identity uses `Rpc.Method` and a `*Methods` member.
	//
	// It is one function because it was three: `PlayerAgentMethodIds.ObservedOpcode`, the importer's dump comparisons
	// and the dialogue decoder's header scan each spelled the same shift out longhand.
	public static class WireOpcode
	{
		// The method field's protobuf tag (field 4, wire type 0) in the high byte - so method 0 reads as `0x2000`,
		// which is why every interface's first method used to be written that way.
		public const ushort MethodTag = 0x2000;

		// `WriteVarint`'s first byte for this constant - its low 7 bits, with the continuation bit set when more groups
		// follow - under the tag. Defined in terms of the encoding rather than restating it as a magic mask.
		public static ushort Of(uint methodInstance)
		{
			return (ushort)(MethodTag | (methodInstance <= 0x7F
				? methodInstance
				: (methodInstance & 0x7F) | 0x80));
		}

		public static ushort Of(Enum method)
		{
			return Of(Convert.ToUInt32(method));
		}
	}
}

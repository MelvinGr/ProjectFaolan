using System;

namespace Faolan.Core.Network.Protocol
{
	/// <summary>
	///     The two bytes a hex dump shows for an RpcHeader's method field: the field's tag <c>0x20</c>, then the
	///     varint's first group. It is what the Wireshark plugin labels "Opcode" and what <c>tshark_dump.sh</c> records
	///     as <c>"Opcode"</c> in a <c>.tshark.json</c>.
	/// </summary>
	/// <remarks>
	///     It is a rendering of <c>Rpc.Method</c>, never an identity. The view is not injective: it keeps 7 bits of the
	///     constant plus a continuation flag, so on the hash-registered PlayerAgent/PlayerInterface pair two methods
	///     share it - 0x20EC is both <c>ReceivedCharacterSettings</c> and <c>AuthenticateLoggedIn</c>. On the fifteen
	///     index-registered classes it happens to be exact, and "exact on most classes" is exactly what makes it unsafe
	///     as a key.
	///     <para>
	///         It exists for the two jobs a rendering is for, and no others: naming a frame being looked at in a capture
	///         or a dump (docs/protocol/gen/opcodes.md, the timeline report), and meeting a value recorded in this space
	///         by something outside this codebase - the tshark dumps' <c>CapturedPacket.WireOpcode</c>, and the u16 that
	///         ends a captured rpcHeader. Anything that dispatches, keys a table, or declares a codec's identity uses
	///         <c>Rpc.Method</c> and a <c>*Methods</c> member.
	///     </para>
	/// </remarks>
	public static class WireOpcode
	{
		// The method field's protobuf tag (field 4, wire type 0) in the high byte, so method 0 reads as `0x2000`.
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

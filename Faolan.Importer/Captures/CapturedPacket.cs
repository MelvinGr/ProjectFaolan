using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     A captured on-wire packet with the metadata the extractors need. Constructed by TsharkCaptureReader;
	///     consumed by every extractor and importer.
	/// </summary>
	/// <remarks>
	///     The defaulted trailing parameters are dump metadata rather than wire content: they describe where a packet
	///     sat in the capture, not what it says. Extractors ignore them; the `--report timeline` reader needs them to
	///     say when, on which service tier and in which direction something happened.
	/// </remarks>
	public readonly record struct CapturedPacket(
		long Ticks, bool IsDownload, uint WireOpcode, uint SubOpcode, byte[] Bytes,
		int Frame = 0, string Direction = "", int Port = 0, string Protocol = "Conan",
		uint SenderClass = 0, uint ReceiverClass = 0, uint MethodId = 0, int Stream = -1)
	{
		/// <summary>
		///     Renders a method enum member in the space the positional <c>WireOpcode</c> records, for filtering.
		/// </summary>
		/// <remarks>
		///     <c>WireOpcode</c> is a `Protocol.WireOpcode` - the method field's first two bytes as one u16 - read
		///     verbatim out of the dump's `"Opcode"` field. It stays in that recorded space rather than being converted to
		///     a method at the reader because the conversion is not total: on the index-registered classes the low byte
		///     is the method, but on the hash-registered PlayerAgent/PlayerInterface pair it keeps 7 of 32 bits and can
		///     name two methods at once.
		///     The method enums hold `method_instance` itself (`GameAgentMethods.PassBlob = 0x00`) while the property
		///     holds the two bytes recorded for it (`0x2000`), so `WireOpcode == (uint)SomeMethods.X` compiles and is
		///     silently false. This is the one place the two spaces are bridged; every extractor's filter goes through
		///     it instead of a bare cast. The call is fully qualified because `WireOpcode` unqualified is this record's
		///     own positional property.
		/// </remarks>
		public static uint Wire(Enum method)
		{
			return Core.Network.Protocol.WireOpcode.Of(method);
		}

		/// <summary>
		///     Whether this packet is the PassBlob IIR carrier - the one whose body is a length-prefixed IIR record rather
		///     than a typed RPC argument list. Almost every extractor opens by filtering on it.
		/// </summary>
		/// <remarks>
		///     `PassBlob` c2s (GameAgentMethods) and `DispatchPassBlob` s2c (GameInterfaceMethods) are the two directions
		///     of one method on one conversation (GameAgent to GameInterface), so the direction is not part of the test;
		///     call sites that care pin <c>IsDownload</c> themselves.
		/// </remarks>
		public bool IsIirCarrier => WireOpcode == Wire(GameAgentMethods.PassBlob);

		/// <summary>
		///     The body within the full on-wire packet: [len4][crc4][headerLen4][header][body]. Empty when the packet is
		///     too short to carry the fixed 12-byte prologue, or when its header length runs past the end.
		/// </summary>
		public byte[] Body
		{
			get
			{
				if (Bytes.Length < 12)
					return [];

				var headerLen = (int)Bytes.ReadU32Be(8);
				var bodyStart = 12 + headerLen;
				return bodyStart <= Bytes.Length
					? Bytes[bodyStart..]
					: [];
			}
		}

		/// <summary>
		///     The IIR record an <see cref="IsIirCarrier" /> packet carries. Empty when the packet cannot carry one (too
		///     short, or a header length running past the end).
		/// </summary>
		/// <remarks>
		///     The body is `[u32 len][record]`, and a codec owns the record from its type word on - the length word
		///     belongs to the framing sender, so handing a codec the whole body would fail its `EnsureAtEnd()` by four
		///     bytes at the front. This is frame work, not decode: it slices the carrier, it does not read a field. The
		///     slice is bounded by the declared length, once, here: a record that declares less than the body holds has a
		///     tail that is not its own.
		/// </remarks>
		public ReadOnlySpan<byte> Record => CapturedRecords.Record(Bytes);
	}
}

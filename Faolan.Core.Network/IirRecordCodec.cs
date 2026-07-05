using System;
using System.Buffers.Binary;

namespace Faolan.Core.Network
{
	// Decomposes a world-burst packet into a structured IirRecord and rebuilds the exact bytes back.
	//
	// Every burst packet is `RpcHeader + <opcode> + <body>`. The RpcHeader is self-describing: the combined endpoint
	// section + opcode length is a big-endian u32 at offset 8, so the body always starts at `12 + that`. For an IIR
	// download (opcode 0x2000) the body is `[u32 bodyLen][u32 IirType][u32 TypeTag][u32 Handle][u32 FieldA][payload]`
	// where bodyLen counts everything after itself (= 16 + payload). We lift IirType/TypeTag/Handle/FieldA into
	// columns, derive bodyLen on rebuild, and keep the framing (Header = RpcHeader + opcode marker) + the residual
	// payload (Body) verbatim. Any other opcode (e.g. the 0x2076 entity-index manifest) is stored Header + Body.
	//
	// Rebuild is byte-for-byte identical to Parse's input - proven a round-trip over all 3283 captured packets across
	// all 10 maps - so the proven byte-preserving replay (RewriteBurstInstance, in-place) is unaffected.
	public static class IirRecordCodec
	{
		public const ushort IirOpcode = 0x2000;
		private const int EndpointLengthOffset = 8; // u32 BE: endpoint section + opcode length, measured from offset 12
		private const int IirHeaderFields = 20; // bodyLen + IirType + TypeTag + Handle + FieldA

		// Splits a raw burst packet into its structured form. `sequence` is the packet's order within the burst.
		public static IirRecord Parse(byte[] packet, int sequence)
		{
			if (packet == null)
				throw new ArgumentNullException(nameof(packet));

			var bodyStart = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(EndpointLengthOffset, 4));
			var opcode = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(bodyStart - 2, 2));

			var record = new IirRecord
			{
				Sequence = sequence,
				Opcode = opcode,
				Header = packet[..bodyStart]
			};

			if (opcode == IirOpcode)
			{
				// [u32 bodyLen]@bodyStart is derived on rebuild; the four header fields follow it.
				record.IirType = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 4, 4));
				record.TypeTag = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 8, 4));
				record.Handle = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 12, 4));
				record.FieldA = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(bodyStart + 16, 4));
				record.Body = packet[(bodyStart + IirHeaderFields)..];
			}
			else
				record.Body = packet[bodyStart..];

			return record;
		}

		// Reconstructs the exact original packet bytes from a structured record.
		public static byte[] Rebuild(IirRecord record)
		{
			if (record == null)
				throw new ArgumentNullException(nameof(record));

			var header = record.Header ?? [];
			var body = record.Body ?? [];

			if (record.Opcode == IirOpcode)
			{
				var buffer = new byte[header.Length + IirHeaderFields + body.Length];
				header.CopyTo(buffer, 0);
				var o = header.Length;
				BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o, 4), (uint)(16 + body.Length)); // bodyLen
				BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o + 4, 4), record.IirType);
				BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o + 8, 4), record.TypeTag);
				BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o + 12, 4), record.Handle);
				BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o + 16, 4), record.FieldA);
				body.CopyTo(buffer, o + IirHeaderFields);
				return buffer;
			}

			var plain = new byte[header.Length + body.Length];
			header.CopyTo(plain, 0);
			body.CopyTo(plain, header.Length);
			return plain;
		}
	}
}

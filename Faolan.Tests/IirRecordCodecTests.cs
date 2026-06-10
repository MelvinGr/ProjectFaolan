using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Network;
using Xunit;

namespace Faolan.Tests
{
	// Locks the lossless round-trip of IirRecordCodec: a world-burst packet decomposed into a structured
	// MapWorldBurstEntity and rebuilt must be byte-for-byte identical to the original. This is the safety net that
	// lets the burst live as queryable IIR rows instead of opaque blobs while the proven byte-preserving replay
	// (RewriteBurstInstance in-place) keeps working. (The exhaustive proof is over all 3283 captured packets across
	// all 10 maps; these synthetic packets reproduce the exact framing and lock the codec's behaviour.)
	public class IirRecordCodecTests
	{
		[Fact]
		public void Parse_LiftsIirHeaderFields()
		{
			var endpoints = Endpoints()
				.First();
			var packet = BuildPacket(endpoints, IirRecordCodec.IirOpcode, IirBody(0x5D85BFC7, 0xABCD, 0x00081906, 0x00000F42, [0xAA, 0xBB]));

			var record = IirRecordCodec.Parse(packet, 7);
			Assert.Equal(IirRecordCodec.IirOpcode, record.Opcode);
			Assert.Equal(0x5D85BFC7u, record.IirType);
			Assert.Equal(0xABCDu, record.TypeTag);
			Assert.Equal(0x00081906u, record.Handle);
			Assert.Equal(0x00000F42u, record.FieldA);
			Assert.Equal(7, record.Sequence);
			Assert.Equal(new byte[] { 0xAA, 0xBB }, record.Body);
		}

		[Fact]
		public void Parse_NonIirOpcode_KeepsWholeBody()
		{
			var endpoints = Endpoints()
				.First();
			var manifest = BuildPacket(endpoints, 0x2076, [0x01, 0x02, 0x03]);

			var record = IirRecordCodec.Parse(manifest, 0);
			Assert.Equal((ushort)0x2076, record.Opcode);
			Assert.Equal(0u, record.IirType);
			Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, record.Body);
		}

		[Fact]
		public void ParseThenRebuild_IsByteIdentical_ForIirAndPlainPackets()
		{
			foreach (var endpoints in Endpoints())
			{
				// 0x2000 IIR record with a payload of arbitrary length.
				var iir = BuildPacket(endpoints, IirRecordCodec.IirOpcode, IirBody(0xFA015807, 0xC350, 0x00081906, 0, [1, 2, 3, 4, 5, 6, 7]));
				Assert.True(iir.SequenceEqual(IirRecordCodec.Rebuild(IirRecordCodec.Parse(iir, 0))));

				// Empty payload (handle-only IIR, e.g. a global record).
				var empty = BuildPacket(endpoints, IirRecordCodec.IirOpcode, IirBody(0x96B8DC59, 0xC350, 0, 0x0F42, []));
				Assert.True(empty.SequenceEqual(IirRecordCodec.Rebuild(IirRecordCodec.Parse(empty, 1))));

				// A non-0x2000 framing opcode (e.g. the 0x2076 entity-index manifest) is stored Header + Body verbatim.
				var manifest = BuildPacket(endpoints, 0x2076, [0x00, 0x00, 0x3B, 0x1F, 0x00, 0x00, 0x00, 0x04]);
				Assert.True(manifest.SequenceEqual(IirRecordCodec.Rebuild(IirRecordCodec.Parse(manifest, 2))));
			}
		}

		// Builds a packet with the real framing: [u32 size][u32 hash][u32 innerLen][endpoints][opcode] then the body.
		// innerLen (at offset 8) covers the endpoint section + the 2-byte opcode, so bodyStart = 12 + innerLen.
		private static byte[] BuildPacket(byte[] endpoints, ushort opcode, byte[] afterOpcode)
		{
			var innerLen = endpoints.Length + 2;
			var packet = new byte[12 + innerLen + afterOpcode.Length];
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)packet.Length); // size (cosmetic here)
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), 0xDEADBEEF); // hash (cosmetic here)
			BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), (uint)innerLen);
			endpoints.CopyTo(packet, 12);
			BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(12 + endpoints.Length, 2), opcode);
			afterOpcode.CopyTo(packet, 12 + innerLen);
			return packet;
		}

		// A few endpoint-section lengths (varying instance varint widths) to exercise the bodyStart computation.
		private static IEnumerable<byte[]> Endpoints()
		{
			return
			[
				new byte[] { 0x0A, 0x07, 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x58, 0x12, 0x0A, 0x0D, 0x47, 0xC1, 0x67, 0x6C, 0x10, 0xFF, 0xBD, 0xD9, 0x10 },
				new byte[] { 0x0A, 0x05, 0x0D, 0x01, 0x02, 0x03, 0x04, 0x12, 0x08, 0x0D, 0x05, 0x06, 0x07, 0x08, 0x10, 0x42 }
			];
		}

		private static byte[] IirBody(uint iirType, uint typeTag, uint handle, uint fieldA, byte[] payload)
		{
			var body = new byte[20 + payload.Length];
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)(16 + payload.Length)); // bodyLen
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), iirType);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8, 4), typeTag);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(12, 4), handle);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), fieldA);
			payload.CopyTo(body, 20);
			return body;
		}
	}
}

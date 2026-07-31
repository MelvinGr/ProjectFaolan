using System;
using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The world dynel's identity MESSAGE - the length-prefixed ProtoSimpleDynelIIR that sits between the base-class
	// transform and the geometry record inside every dynel full-update body (#187 / #57). The reader was decoded by
	// VTABLE-xref (docs/re/iir-subclass-readers.md, FUN_007a2510 -> FUN_00cbfb80 ReadLengthPrefixedBlob):
	//   [u32 BE blobLen][ProtoSimpleDynelIIR protobuf]
	// protobuf-net round-trips the captured wire bytes losslessly (SimpleDynelMessageTests), so name / display_name /
	// cars_group / template_item_id / flags are typed rather than opaque.
	//
	// A class-(c) SUB-RECORD codec (#530 rule 5c): composed by WorldObjectCodec, so it declares no opcode.
	public static class SimpleDynelIdentityCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ProtoSimpleDynelIIR message)
		{
			ArgumentNullException.ThrowIfNull(message);
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, message);
			var protobuf = ms.ToArray();

			var outBytes = new byte[4 + protobuf.Length];
			outBytes.WriteU32Be(0, (uint)protobuf.Length);
			protobuf.CopyTo(outBytes, 4);
			return outBytes;
		}

		public static ProtoSimpleDynelIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SimpleDynelIdentityCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ProtoSimpleDynelIIR Read(ref SpanReader r)
		{
			// [u32 BE blobLen][protobuf] - a BIG-ENDIAN word, not the varint length protobuf itself would use.
			return Serializer.Deserialize<ProtoSimpleDynelIIR>(r.ReadBytesU32Length().ToArray().AsMemory());
		}
	}
}

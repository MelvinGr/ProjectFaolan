using System;
using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The loot bag's CONTENTS message - cash + looter handles + item inventory (#187). A loot bag's body is a plain
	// dynel plus a SECOND length-prefixed blob at the very end:
	//   [u32 len][ProtoSimpleDynelIIR identity][kd-tree/bounds struct][u32 len][ProtoLootBagIIR contents]
	// (reader decoded by vtable-xref, docs/re/iir-subclass-readers.md, FUN_007a3db0). This codec is that trailing
	// blob, rebuilt BYTE-EXACT - protobuf-net round-trips the captured wire bytes losslessly, including the nested
	// ProtoInventoryGame (LootBagMessageTests).
	//
	// It is what makes a loot bag's contents CONSTRUCTIBLE from the live drop (cash + looter + the killed entity's
	// items) instead of replaying a captured template's contents. NB: flipping MakeCorpseLootable to send constructed
	// contents is in-client-gated - changing a loot bag's on-wire contents mid-session is the substitution risk noted
	// in the spawn-render memory; this codec is the proven building block for when that's verified.
	//
	// A class-(c) SUB-RECORD codec (#530 rule 5c): composed by WorldObjectCodec (the LootBagFullUpdateIIR body), so it
	// declares no opcode of its own - the opcode is that parent's.
	public static class LootBagContentsCodec
	{
		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ProtoLootBagIIR contents)
		{
			ArgumentNullException.ThrowIfNull(contents);
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, contents);
			var protobuf = ms.ToArray();

			var outBytes = new byte[4 + protobuf.Length];
			outBytes.WriteU32Be(0, (uint)protobuf.Length);
			protobuf.CopyTo(outBytes, 4);
			return outBytes;
		}

		public static ProtoLootBagIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(LootBagContentsCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ProtoLootBagIIR Read(ref SpanReader r)
		{
			// [u32 BE blobLen][protobuf] - a BIG-ENDIAN word, not the varint length protobuf itself would use.
			return Serializer.Deserialize<ProtoLootBagIIR>(r.ReadBytesU32Length().ToArray().AsMemory());
		}
	}
}

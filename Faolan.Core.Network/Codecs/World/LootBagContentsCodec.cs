using System;
using System.IO;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     The loot bag's contents message - cash, looter handles and item inventory. A loot bag's body is a plain dynel
	///     plus a second length-prefixed blob at the very end:
	///     <c>[u32 len][ProtoSimpleDynelIIR identity][kd-tree/bounds struct][u32 len][ProtoLootBagIIR contents]</c>; the
	///     reader is FUN_007a3db0, see docs/re/iir-subclass-readers.md. This codec is that trailing blob.
	/// </summary>
	/// <remarks>
	///     It is rebuilt byte-exact: protobuf-net round-trips the captured wire bytes losslessly, including the nested
	///     ProtoInventoryGame (LootBagMessageTests), which is what makes a loot bag's contents constructible from the live
	///     drop rather than replayed from a captured template.
	///     <para>
	///         A sub-record codec: composed by <see cref="WorldObjectCodec" /> as the LootBagFullUpdateIIR body, so it
	///         declares no opcode of its own - the opcode is that parent's.
	///     </para>
	/// </remarks>
	public static class LootBagContentsCodec
	{
		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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
			// [u32 blobLen][protobuf] - a big-endian word, not the varint length protobuf itself would use.
			return Serializer.Deserialize<ProtoLootBagIIR>(r.ReadBytesU32Length().ToArray().AsMemory());
		}
	}
}

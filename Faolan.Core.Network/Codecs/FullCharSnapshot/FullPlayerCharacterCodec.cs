using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// The FullChar (0x737A6DF9) snapshot RECORD - the world-entry player state, both directions in one type.
	//
	//   [u32 FullPlayerCharacterIIR][u32 typeTag = Character][u32 instance][u8 flag][u32 rootLength][root][sub-class]
	//
	// The [flag][rootLength] pair is the client's blob-length frame (n3InfoItemRemote::Construct -> FUN_00cbfb80):
	// the client reads exactly rootLength bytes, and those bytes are the WHOLE ProtoFullCharacterIIR root. The root
	// is decoded/encoded TYPED (protobuf-net, IExtensible => lossless); the sub-class is
	// FullCharSnapshotSubClassCodec. A pure structural split/assembly - no byte scanning.
	//
	// The record's own u32 carrier length belongs to the SENDER (#530 rule 2), not here.
	//
	// ⚠️ WHY READ AND WRITE LIVE TOGETHER. They used to be split across two projects that could not see each other:
	// the reader here, and the prefix builder + assembly on GameServerListener in the server host. Nothing forced
	// them to agree, and they did not. The reader skipped a 3-byte "field5" after the length; the writer appended
	// those same 3 bytes verbatim. That "field5" was never a frame - it is the root's OWN first field
	// (`subscription_flags`, 5), which happens to encode as exactly 3 bytes on the two level-1 beach captures the
	// model came from. So both halves were self-consistent on the beach and every golden passed, while the reader
	// failed on EVERY level-80 snapshot (silently, inside the importer's catch - imported characters came out with
	// no carried inventory) and the writer put subscription_flags on the wire twice.
	//
	// One type, one statement of the model, with a Build/Parse round-trip gate over it - that is what makes the
	// drift impossible rather than merely unlikely. Same class of bug as the 0x7E2 "constant", see
	// [[list_frame_0x3f1_count_word]]: a shape that is "constant across N captures" means nothing when all N
	// captures are the degenerate case.
	public static class FullPlayerCharacterCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FullPlayerCharacterIIR;

		// [u8 flag][u32 BE rootLength]. The flag is 0 on every captured snapshot; asserted rather than skipped, so
		// a body that carries something else there fails loudly instead of shifting the root by a byte.
		private const byte BlobFlag = 0x00;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(FullPlayerCharacterIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static FullPlayerCharacterIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FullPlayerCharacterCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static FullPlayerCharacterIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "FullPlayerCharacterIIR type word");
			r.ExpectU32Be(EntityClass.Character, "type tag");
			var instance = r.ReadU32Be();

			r.ExpectU8(BlobFlag, "blob-length frame flag");
			var root = r.ReadBytes(checked((int)r.ReadU32Be())).FromProtobuf<ProtoFullCharacterIIR>();
			var subClass = FullCharSnapshotSubClassCodec.Read(ref r);

			return new FullPlayerCharacterIIR(instance, root, subClass);
		}

		internal static void Write(FaolanStream s, FullPlayerCharacterIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);

			var root = v.Root.ToProtobuf();

			s.WriteUInt32(Opcode);
			s.WriteUInt32(EntityClass.Character);
			s.WriteUInt32(v.Instance);
			s.WriteByte(BlobFlag);
			s.WriteUInt32((uint)root.Length); // blobLen counts the root exactly, so the frame carries no payload
			s.WriteArray(root);
			FullCharSnapshotSubClassCodec.Write(s, v.SubClass);
		}
	}

	// The snapshot: whose it is, the typed inventory root, and the tails.
	public sealed record FullPlayerCharacterIIR(uint Instance, ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass);
}

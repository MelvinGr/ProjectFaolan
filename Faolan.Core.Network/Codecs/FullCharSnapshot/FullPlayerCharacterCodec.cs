using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     The FullChar snapshot record - the world-entry player state, both directions in one type:
	///     <c>[u32 FullPlayerCharacterIIR][u32 typeTag][u32 instance][u8 flag][u32 rootLength][root][sub-class]</c>.
	/// </summary>
	/// <remarks>
	///     The <c>[flag][rootLength]</c> pair is the client's blob-length frame
	///     (<c>n3InfoItemRemote::Construct</c> into FUN_00cbfb80): the client reads exactly <c>rootLength</c> bytes, and
	///     those bytes are the whole <c>ProtoFullCharacterIIR</c> root. The root is decoded and encoded typed
	///     (protobuf-net, <c>IExtensible</c>, so lossless); the sub-class is
	///     <see cref="FullCharSnapshotSubClassCodec" />. A pure structural split and assembly, with no byte scanning. The
	///     record's own u32 carrier length belongs to the sender.
	///     <para>
	///         Read and write live together deliberately. Split across two projects that cannot see each other, nothing
	///         forces the two halves to agree: a reader that skips a 3-byte "field5" after the length and a writer that
	///         appends those same bytes are each self-consistent, while that "field5" is really the root's own first
	///         field (<c>subscription_flags</c>), which happens to encode as exactly 3 bytes on a level-1 beach capture.
	///         One type, one statement of the model, with a Build/Parse round-trip gate over it, is what makes that drift
	///         impossible rather than merely unlikely - the same class of trap as reading a single-element list frame as
	///         a constant.
	///     </para>
	/// </remarks>
	public static class FullPlayerCharacterCodec
	{
		public const uint Opcode = (uint)IirTypes.FullPlayerCharacterIIR;

		// [u8 flag][u32 BE rootLength]. The flag is 0 on every captured snapshot; asserted rather than skipped, so a body
		// that carries something else there fails loudly instead of shifting the root by a byte.
		private const byte BlobFlag = 0x00;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The snapshot: whose it is, the typed inventory root, and the tails.</summary>
	public sealed record FullPlayerCharacterIIR(uint Instance, ProtoFullCharacterIIR Root, FullCharSnapshotSubClass SubClass);
}

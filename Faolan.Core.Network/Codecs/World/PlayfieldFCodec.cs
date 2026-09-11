using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// PlayfieldFIIR (MapChange) - tells the client which playfield/instance it is entering: its reference position,
	// the map id, the entering character instance, and the live per-session playfield token (carried twice).
	//
	// Framed the way the client reads it. Its reader is a two-part chain: PlayfieldFIIR_c's own ReadSubClass is
	// FUN_008f36e0, which calls the base FUN_013d0a20 and then reads the four trailing words. The offsets below are
	// the common single-scene-entry case; see the note on the blob's length under it:
	//
	//   [u32 subop]                        the IIR type word
	//   [u32 class][u32 handle][u8 env]    the IIR base carrier                            @0..8
	//   [f32 X][f32 Y][f32 Z]              -> obj+0x24 / +0x28 / +0x2C                     @9..20
	//   [PlayfieldProxy]                   the FUN_00a6a600 stream, embedded here          @21..54
	//   [u32 sceneWord]                    -> obj+0x54                                     @55..58
	//   [u32 len = 24][24-byte blob]       the FUN_00cbf5d0 length-prefixed sub-stream      @59..86
	//   [u32][u32][u32 fieldC][u32 flags]  -> obj+0x80 / +0x84 / +0x88 / +0x8C             @87..102
	//
	// Validated over the captured PlayfieldFIIR bodies: every one carries the proxy's 'b' version byte at @21.
	//
	// The body is not a fixed size. The blob is length-prefixed and its first word is a count (see SingleSceneEntry
	// below), so the body is 59 + 4 + blobLen + 16. Three sizes are captured: blobLen 24 (one entry, the common case,
	// 103 bytes total), 32 (two entries, the Amphitheatre of Karutonia) and 4 (zero entries, 83 bytes total) - the
	// last being White Sands Night (10803) and every other instanced playfield entered the same way.
	//
	// Neither `sceneWord` nor the blob's last word is semantically pinned - `sceneWord` is 0 on maps 4020/4022 and 1
	// on 1000/1010/2000/2010/2040/3000/5007/11040 while 4000 and 4010 carry both, and `blobLastWord` takes 0/1/2/3
	// within a single map - so both are still passed through. [V0 on their meaning; V1 on the framing.]
	//
	// The blob is an opaque pass-through: the client copies it in and echoes it back, and never reads a field out of
	// it. That is why nothing about its contents correlates with anything.
	//
	//   read  `FUN_013d0a20`: stores position (obj+0x24/+0x28/+0x2C), calls the proxy reader, stores `sceneWord`
	//         (obj+0x54), then hands the length-prefixed blob to `FUN_00cbf5d0` - the generic `BinaryStream.cpp`
	//         sub-stream copier - which parks the bytes in an embedded stream at obj+0x5C.
	//   write `FUN_013d0aa0`: the exact mirror, ending in `FUN_00cbf550`, which emits those bytes verbatim.
	//
	// Every other value in this body lands in a named slot. The blob's do not, and nothing else in the class touches
	// obj+0x5C - not the base reader/writer, not any of the four vtable siblings around the reader
	// (`008f30f0` returns obj+0x90, `008f3740` is the writer, `008f38a0` forwards to `008f3190`). The class exposes
	// no accessor to it either, so there is no obvious route for an outside consumer.
	//
	// The two asserts name both classes for free: `iir\playfieldFIIR.cpp` and the base
	// `IIR\n3playfieldfullupdateiir.cpp`.
	//
	// Read/Write do parse the blob into `BlobFloat1`/`BlobFloat2`/`BlobLastWord`, and that structure is real - all 218
	// captured bodies decode and re-encode byte-exact. But it is structure the client does not look at, so do not
	// reason from those names about behaviour, and do not expect changing them to alter anything on screen.
	//
	// The two floats are named by position deliberately. Measured over all 218 bodies in the dumps, the first is 15.0
	// in every one and the second ranges 0.053 .. 14.796, always below it; the second correlates with neither the
	// in-game clock (r = -0.01 against GameTimeIIR's DayTimeSeconds over 215 records) nor wall-clock time mod 15
	// (r = +0.13), and is spread uniformly across [0, 15). Nothing supports a camera-clip reading, so the names state
	// position only and a later decode can replace them. Our sender hardcodes the pair as (15.0, 7.111854), which
	// matches retail exactly on the first and is a single frozen value where retail varies the second.
	public static class PlayfieldFCodec
	{
		public const uint Opcode = (uint)IirTypes.PlayfieldFIIR;

		// The scene blob's leading word is a count, not the constant 1 it looks like on 217 of the 218 captured bodies.
		// The one record carrying 2 is the team-owned Amphitheatre of Karutonia entry, and its blob is 32 bytes rather
		// than 24: the modelled entry, then eight more bytes `[the same token][0x0000CB24]`. So a second entry exists and
		// is shorter than the first, i.e. the entry is kind-tagged (0xCB20 vs 0xCB24) rather than fixed-width.
		// Deliberate interim: the extra entries ride as `SceneBlobTail` bytes so the record round-trips, because one
		// sample cannot pin a kind-tagged grammar and the client does not decode the blob where it reads it
		// (FUN_00CBF5D0 copies it into a nested stream for a later consumer).
		public const uint SingleSceneEntry = 0x00000001;
		private const uint SceneBlobWord2 = 0x0000CB20;

		// The two words at obj+0x80 / +0x84, 0xFFFFFFFF in every capture.
		private const uint TrailerSentinel = 0xFFFFFFFF;

		// Canonical - the only method here that writes bytes.
		public static byte[] Build(PlayfieldFIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(Vector3 position, uint mapId, uint charInstance, uint count, uint playfieldToken, float blobFloat1, float blobFloat2, uint flags,
			uint sceneWord = 0u, uint blobLastWord = 0u, uint fieldA = 0u, uint fieldC = 7u, uint classWord = PlayfieldProxy.PlayfieldClass)
		{
			return Build(new PlayfieldFIIR(position,
				new PlayfieldProxy(mapId, classWord, charInstance, count, PlayfieldProxy.PinnedInstanceWord, playfieldToken, OwnerType: fieldA),
				sceneWord, blobFloat1, blobFloat2, blobLastWord, fieldC, flags));
		}

		public static PlayfieldFIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PlayfieldFCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static PlayfieldFIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "PlayfieldFIIR subop");
			var classWord = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var position = r.ReadVector3Be();
			var proxy = PlayfieldProxy.Read(ref r);
			var sceneWord = r.ReadU32Be();

			// The length-prefixed sub-blob: the client reads it through FUN_00cbf5d0, so its contents are a nested
			// stream and not four more top-level words.
			var blob = new SpanReader(r.ReadBytesU32Length(), nameof(PlayfieldFCodec) + ".sceneBlob");
			var sceneEntries = blob.ReadU32Be();

			// A count of zero means the blob is the count word and nothing else - 4 bytes, not 24. White Sands Night
			// (10803) sends that, so the entry must never be read unconditionally.
			var blobToken = proxy.InstanceToken;
			var blobFloat1 = 0f;
			var blobFloat2 = 0f;
			var blobLastWord = 0u;
			var blobTail = Array.Empty<byte>();
			if (sceneEntries > 0)
			{
				blobToken = blob.ReadU32Be(); // the token again, inside the blob
				blob.ExpectU32Be(SceneBlobWord2, "scene blob word 2");
				blobFloat1 = blob.ReadF32Be(); // 15.0 in all 218 captured bodies
				blobFloat2 = blob.ReadF32Be(); // 0.053 .. 14.796, always below it
				blobLastWord = blob.ReadU32Be(); // beach capture carries 0x3, retail Old Tarantia 0x0
				blobTail = blob.ReadRemaining().ToArray();
			}

			blob.EnsureAtEnd();

			r.ExpectU32Be(TrailerSentinel, "trailer word obj+0x80");
			r.ExpectU32Be(TrailerSentinel, "trailer word obj+0x84");
			var fieldC = r.ReadU32Be(); // per-map: 0 on 1010/2010/2040/5007 and 6 on 11040
			var flags = r.ReadU32Be();

			if (blobToken != proxy.InstanceToken)
				throw CodecParseException.Unexpected(nameof(PlayfieldFCodec), r.Position, $"the scene blob to repeat the proxy's playfield token 0x{proxy.InstanceToken:X8}", $"0x{blobToken:X8}");

			return new PlayfieldFIIR(position, proxy, sceneWord, blobFloat1, blobFloat2, blobLastWord, fieldC, flags, classWord, handle, env, sceneEntries, blobTail);
		}

		internal static void Write(FaolanStream s, PlayfieldFIIR v)
		{
			using var blob = new FaolanStream();
			blob.WriteUInt32(v.SceneEntries);
			// A zero-entry blob is the count word alone - see Read.
			if (v.SceneEntries > 0)
				blob.WriteUInt32(v.Proxy.InstanceToken).WriteUInt32(SceneBlobWord2).WriteFloat(v.BlobFloat1).WriteFloat(v.BlobFloat2).WriteUInt32(v.BlobLastWord).WriteArray(v.SceneBlobTail ?? []);

			s.WriteUInt32(Opcode).WriteUInt32(v.ClassWord).WriteUInt32(v.Handle).WriteByte(v.Env).WriteVector3(v.Position);
			v.Proxy.Write(s);
			s.WriteUInt32(v.SceneWord).WriteArrayUInt32Length(blob.ToArray()).WriteUInt32(TrailerSentinel).WriteUInt32(TrailerSentinel).WriteUInt32(v.FieldC).WriteUInt32(v.Flags);
		}
	}

	// The map id, the instance owner and the playfield token all live in the embedded PlayfieldProxy - see there for
	// what each of its slots means. ClassWord/Handle/Env are the IIR base carrier; the create-editor and world-entry
	// paths both send them as 0.
	public sealed record PlayfieldFIIR(Vector3 Position, PlayfieldProxy Proxy, uint SceneWord, float BlobFloat1, float BlobFloat2, uint BlobLastWord, uint FieldC, uint Flags,
		uint ClassWord = 0, uint Handle = 0, byte Env = 0, uint SceneEntries = PlayfieldFCodec.SingleSceneEntry, byte[]? SceneBlobTail = null)
	{
		public uint MapId => Proxy.MapId;

		/// <summary>
		///     The per-map word the importer harvests as Map.PlayfieldFieldA - <see cref="PlayfieldProxy" /> slot [2], the
		///     instance owner's ID32 type.
		/// </summary>
		public uint FieldA => Proxy.OwnerType;
	}
}

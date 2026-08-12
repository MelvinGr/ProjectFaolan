using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// PlayfieldFIIR (MapChange, 0x5D85BFC7) - tells the client which playfield/instance it is entering: its reference
	// position, the map id, the entering character instance, and the live per-session playfield token (carried TWICE).
	// 103 bytes after the subop. Was MapChangeCodec, which knew four field OFFSETS into an otherwise opaque body; the
	// whole grammar has been decoded since #451, so the codec now reads it sequentially (#530 rule 3).
	//
	// Framed the way the CLIENT reads it. Its reader is a two-part chain: PlayfieldFIIR_c's own ReadSubClass is
	// FUN_008f36e0, which calls the base FUN_013d0a20 and then reads the four trailing words:
	//
	//   [u32 subop]                        the IIR type word
	//   [u32 class][u32 handle][u8 env]    the IIR base carrier                            @0..8
	//   [f32 X][f32 Y][f32 Z]              -> obj+0x24 / +0x28 / +0x2C                     @9..20
	//   [PlayfieldProxy]                   the FUN_00a6a600 stream, EMBEDDED here          @21..54
	//   [u32 sceneWord]                    -> obj+0x54                                     @55..58
	//   [u32 len = 24][24-byte blob]       the FUN_00cbf5d0 length-prefixed sub-stream      @59..86
	//   [u32][u32][u32 fieldC][u32 flags]  -> obj+0x80 / +0x84 / +0x88 / +0x8C             @87..102
	//
	// Validated over all 151 captured PlayfieldFIIR bodies: every one is 103 bytes, carries the proxy's 'b' version
	// byte at @21, and its blob length word reads exactly 24 so that 59 + 4 + 24 + 16 == 103. Zero failures.
	//
	// Neither `sceneWord` nor the blob's last word is semantically pinned - `sceneWord` is 0 on maps 4020/4022 and 1
	// on 1000/1010/2000/2010/2040/3000/5007/11040 while 4000 and 4010 carry both, and `blobLastWord` takes 0/1/2/3
	// within a single map - so both are still passed through. [V0 on their meaning; V1 on the framing.]
	//
	// ⚠️ **The blob is an OPAQUE PASS-THROUGH: the client copies it in and echoes it back, and never reads a field
	// out of it.** That is why nothing about its contents correlates with anything.
	//
	//   read  `FUN_013d0a20`: stores position (obj+0x24/+0x28/+0x2C), calls the proxy reader, stores `sceneWord`
	//         (obj+0x54), then hands the length-prefixed blob to `FUN_00cbf5d0` - the generic `BinaryStream.cpp`
	//         sub-stream COPIER - which parks the bytes in an embedded stream at **obj+0x5C**.
	//   write `FUN_013d0aa0`: the exact mirror, ending in `FUN_00cbf550`, which emits those bytes verbatim.
	//
	// Every other value in this body lands in a named slot. The blob's do not, and **nothing else in the class
	// touches obj+0x5C** - not the base reader/writer, not any of the four vtable siblings around the reader
	// (`008f30f0` returns obj+0x90, `008f3740` is the writer, `008f38a0` forwards to `008f3190`). The class exposes
	// no accessor to it either, so there is no obvious route for an outside consumer.
	//
	// The two asserts name both classes for free: `iir\playfieldFIIR.cpp` and the base
	// `IIR\n3playfieldfullupdateiir.cpp`.
	//
	// ⚠️ Our own Read/Write DO parse the blob into `BlobFloat1`/`BlobFloat2`/`BlobLastWord`, and that structure is real -
	// all 218 captured bodies decode and re-encode byte-exact. But it is structure the CLIENT does not look at, so
	// do not reason from those names about behaviour, and do not expect changing them to alter anything on screen.
	//
	// ⚠️ They were called `NearClip`/`FarClip` until 2026-08-09, and the second name could not be right. Measured
	// over all 218 bodies
	// in the dumps: the first float is **15.0 in every single one**, and the second ranges **0.053 .. 14.796**,
	// always BELOW it. A far clip nearer than the near clip is not a camera. The second correlates with neither the
	// in-game clock (r = -0.01 against GameTimeIIR's DayTimeSeconds over 215 records) nor wall-clock time mod 15
	// (r = +0.13); it is spread uniformly across [0, 15). They are named by POSITION now, like `BlobLastWord`
	// beside them - a name a later decode can replace, rather than one that asserts a camera. Our sender hardcodes
	// the pair as (15.0, 7.111854), which
	// matches retail exactly on the first and is a single frozen value where retail varies the second.
	public static class PlayfieldFCodec
	{
		public const uint Opcode = (uint)IirTypes.PlayfieldFIIR;

		// The scene blob's leading word is a COUNT, not the constant 1 it looks like on 217 of the 218 captured
		// bodies (#811 - the same trap the ListFrame notes describe, and this one is not even framed by 0x3F1). The
		// one record carrying 2 is the TEAM-owned Amphitheatre of Karutonia entry, and its blob is 32 bytes rather
		// than 24: the modelled entry, then eight more bytes `[the same token][0x0000CB24]`. So a second entry exists
		// and is SHORTER than the first, i.e. the entry is kind-tagged (0xCB20 vs 0xCB24) rather than fixed-width.
		// ⚠️ Deliberate interim: the extra entries ride as `SceneBlobTail` bytes so the record round-trips, because
		// one sample cannot pin a kind-tagged grammar and the client does not decode the blob where it reads it
		// (FUN_00CBF5D0 copies it into a nested stream for a later consumer).
		public const uint SingleSceneEntry = 0x00000001;
		private const uint SceneBlobWord2 = 0x0000CB20;

		// The two words at obj+0x80 / +0x84, 0xFFFFFFFF in every capture.
		private const uint TrailerSentinel = 0xFFFFFFFF;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(PlayfieldFIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(Vector3 position, uint mapId, uint charInstance, uint count, uint playfieldToken, float blobFloat1, float blobFloat2, uint flags,
			uint sceneWord = 0u, uint blobLastWord = 0u, uint fieldA = 0u, uint fieldC = 7u)
		{
			return Build(new PlayfieldFIIR(position,
				new PlayfieldProxy(mapId, PlayfieldProxy.PlayfieldClass, charInstance, count, PlayfieldProxy.PinnedInstanceWord, playfieldToken, OwnerType: fieldA),
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
			var blobToken = blob.ReadU32Be(); // the token again, inside the blob
			blob.ExpectU32Be(SceneBlobWord2, "scene blob word 2");
			var blobFloat1 = blob.ReadF32Be(); // 15.0 in all 218 captured bodies
			var blobFloat2 = blob.ReadF32Be(); // 0.053 .. 14.796, always below it
			var blobLastWord = blob.ReadU32Be(); // beach capture carries 0x3, retail Old Tarantia 0x0
			var blobTail = blob.ReadRemaining().ToArray();

			r.ExpectU32Be(TrailerSentinel, "trailer word obj+0x80");
			r.ExpectU32Be(TrailerSentinel, "trailer word obj+0x84");
			var fieldC = r.ReadU32Be(); // per-MAP, 0 on 1010/2010/2040/5007 and 6 on 11040 (#451)
			var flags = r.ReadU32Be();

			if (blobToken != proxy.InstanceToken)
				throw CodecParseException.Unexpected(nameof(PlayfieldFCodec), r.Position, $"the scene blob to repeat the proxy's playfield token 0x{proxy.InstanceToken:X8}", $"0x{blobToken:X8}");

			return new PlayfieldFIIR(position, proxy, sceneWord, blobFloat1, blobFloat2, blobLastWord, fieldC, flags, classWord, handle, env, sceneEntries, blobTail);
		}

		internal static void Write(FaolanStream s, PlayfieldFIIR v)
		{
			using var blob = new FaolanStream();
			blob.WriteUInt32(v.SceneEntries).WriteUInt32(v.Proxy.InstanceToken).WriteUInt32(SceneBlobWord2).WriteFloat(v.BlobFloat1).WriteFloat(v.BlobFloat2).WriteUInt32(v.BlobLastWord).WriteArray(v.SceneBlobTail ?? []);

			s.WriteUInt32(Opcode).WriteUInt32(v.ClassWord).WriteUInt32(v.Handle).WriteByte(v.Env).WriteVector3(v.Position);
			v.Proxy.Write(s);
			s.WriteUInt32(v.SceneWord).WriteArrayUInt32Length(blob.ToArray()).WriteUInt32(TrailerSentinel).WriteUInt32(TrailerSentinel).WriteUInt32(v.FieldC).WriteUInt32(v.Flags);
		}
	}

	// PlayfieldFIIR (0x5D85BFC7). The map id, the instance owner and the playfield token all live in the embedded
	// PlayfieldProxy - see there for what each of its slots means.
	//
	// ClassWord/Handle/Env are the IIR base carrier; the create-editor and world-entry paths both send them as 0.
	public sealed record PlayfieldFIIR(Vector3 Position, PlayfieldProxy Proxy, uint SceneWord, float BlobFloat1, float BlobFloat2, uint BlobLastWord, uint FieldC, uint Flags,
		uint ClassWord = 0, uint Handle = 0, byte Env = 0, uint SceneEntries = PlayfieldFCodec.SingleSceneEntry, byte[]? SceneBlobTail = null)
	{
		public uint MapId => Proxy.MapId;

		// The per-map word the importer harvests as Map.PlayfieldFieldA - PlayfieldProxy slot [2], the instance
		// owner's ID32 TYPE (#512).
		public uint FieldA => Proxy.OwnerType;
	}
}

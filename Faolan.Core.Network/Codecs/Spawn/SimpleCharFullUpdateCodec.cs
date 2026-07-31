using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// SimpleCharFullUpdateIIR (0xFA015807) - the ENTITY SPAWN body, for a player and for an NPC alike:
	//
	//   [u32 subop][u32 class][u32 handle][u32 fieldA]
	//   [varint transformLen][0x0A vec3][0x12 quat][opt 0x25 scale][0x18 playfield varint]
	//   [pre-IIR scaffold]                                     SpawnPreIirCodec
	//   [u8 0x41][u32 coreCharIirHeader][u32 msgLen][msg]      the embedded CoreCharacterFullUpdateIIR
	//   [post-IIR stream]                                      SpawnPostIirCodec
	//
	// ONE codec for the message (#530 rule 5, wave 5b). It used to be THREE emitters of the same IIR:
	// `PlayerSpawnScaffoldCodec` (which declared the opcode), `NpcSpawnBuilder.BuildBody` and
	// `NpcSpawnPreIirCodec` - and two of them wrote the same transform through different framings, exactly the way
	// wave 3's WorldObject merge did: the player wrote the transform's length as the literal `0x2C` and the NPC
	// computed it, which is the same 44 bytes for every 4-byte playfield token and diverges for any other width.
	// Computing it is what makes a short token (a 0 token gives a 41-byte transform) stay self-consistent, and a
	// mismatch there desyncs the client's version read into "Wrong client version of SimpleCharFullupdateIIR".
	//
	// The two families differ in ONE region, the post-IIR entry slot; see SpawnPostIir.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2) - this builds the record only.
	public static class SimpleCharFullUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleCharFullUpdateIIR;

		// The embedded CoreCharacterFullUpdateIIR's version byte, 'A'. The client rejects any other value.
		public const byte CoreCharacterVersion = 0x41;

		// The transform component's protobuf field tags: a ProtoGameCodeDynelIIR
		// { position = 1, rotation = 2, playfield_instance = 3 }. All 15787 captured FA015807/66AEDD50 groups carry
		// exactly those three fields in that order; field 4 (a fixed32) is never observed - see the Scale note on
		// the value type.
		private const byte PositionTag = 0x0A;
		private const byte RotationTag = 0x12;
		private const byte ScaleTag = 0x25;
		private const byte PlayfieldTag = 0x18;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SimpleCharFullUpdateIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SimpleCharFullUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SimpleCharFullUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SimpleCharFullUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SimpleCharFullUpdateIIR subop");

			var value = new SimpleCharFullUpdateIIR
			{
				Ack = r.ReadU32Be(),
				Handle = r.ReadU32Be(),
				FieldA = r.ReadU32Be()
			};

			ReadTransform(ref r, value);
			value.PreIir = SpawnPreIirCodec.Read(ref r);
			value.CoreCharVersion = r.ExpectU8(CoreCharacterVersion, "embedded CoreCharacter version");
			value.CoreCharIirHeader = r.ReadU32Be();
			value.Character = r.ReadBytesU32Length().FromProtobuf<ProtoCoreCharacterIIR>();

			// The post-IIR runs to the end of the record, and its grammar is end-anchored - see SpawnPostIirCodec.
			value.Post = SpawnPostIirCodec.Read(r.ReadRemaining());
			return value;
		}

		internal static void Write(FaolanStream s, SimpleCharFullUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.Ack).WriteUInt32(v.Handle).WriteUInt32(v.FieldA);

			// The transform-length prefix: the client reads EXACTLY this many bytes as the base transform, THEN
			// reads the SimpleCharFullUpdateIIR version byte - so it MUST equal the actual transform length. It is
			// 0x2C (44) for the usual 4-byte playfield-token varint (every captured token falls in [2^21, 2^28),
			// which protobuf writes as 4 bytes), which is why it was long hardcoded on the player side. A 0/small
			// token encodes to a SHORTER varint; a hardcoded 0x2C then over-reads by 3 and the client asserts.
			using var transform = new FaolanStream();
			transform.WriteByte(PositionTag).WriteProtobufByteLength((ProtoVec3)v.Position).WriteByte(RotationTag).WriteProtobufByteLength((ProtoQuat)v.Rotation);
			if (v.Scale != null)
				transform.WriteByte(ScaleTag).WriteArray(v.Scale);

			transform.WriteByte(PlayfieldTag).WriteVarint(v.Playfield);

			var transformBytes = transform.ToArray();
			s.WriteVarint((ulong)transformBytes.Length).WriteArray(transformBytes);

			SpawnPreIirCodec.Write(s, v.PreIir);

			// A U32 length, not a byte one: the CharacterIIR is [u32 msgLen][protobuf], and every real character's
			// message is longer than 255 bytes (Dikketest's is 382), so the byte-length helper throws on everything
			// except a fixture small enough to hide it.
			s.WriteByte(v.CoreCharVersion).WriteUInt32(v.CoreCharIirHeader).WriteProtobufUInt32Length(v.Character);

			SpawnPostIirCodec.Write(s, v.Post);
		}

		private static void ReadTransform(ref SpanReader r, SimpleCharFullUpdateIIR value)
		{
			var length = checked((int)r.ReadVarint());
			var t = new SpanReader(r.ReadBytes(length), nameof(SimpleCharFullUpdateCodec));

			t.ExpectU8(PositionTag, "transform position tag");
			value.Position = t.ReadProtobufByteLength<ProtoVec3>();
			t.ExpectU8(RotationTag, "transform rotation tag");
			value.Rotation = t.ReadProtobufByteLength<ProtoQuat>();
			if (!t.AtEnd && t.PeekRemaining()[0] == ScaleTag)
			{
				t.Skip(1);
				value.Scale = t.ReadBytes(4).ToArray();
			}

			t.ExpectU8(PlayfieldTag, "transform playfield tag");
			value.Playfield = t.ReadVarint();
			t.EnsureAtEnd();
		}
	}

	// SimpleCharFullUpdateIIR (0xFA015807) - a whole entity spawn, per the validated grammar in
	// docs/re/player-spawn-generation.md and docs/re/fa015807-spawn-reader.md.
	public sealed record SimpleCharFullUpdateIIR
	{
		public uint Ack { get; set; } = EntityClass.Character;
		public uint Handle { get; set; }
		public uint FieldA { get; set; }

		// Transform component (the 0x2C group). #352 kept these two as raw field payloads on the grounds that
		// typing "would lose the exact captured field-set" - measured over 11,632 captured player spawns, the field
		// set NEVER varies (vec3 is always {0D,15,1D}, quat always {0D,15,1D,25}). So there is no captured field-set
		// to lose, and the byte-exact regeneration gate over every captured spawn is what says so.
		public Vector3 Position { get; set; }
		public Quaternion Rotation { get; set; }

		// Field 4 as a fixed32, kept RAW - unlike the two above this one is genuinely unmodelled: it is absent from
		// all 11,632 captured player spawns, so the corpus proves nothing about what it would contain, and
		// ProtoGameCodeDynelIIR declares field 4 as `repeated RunningEffects` (length-delimited, not fixed32). If a
		// 0x25 ever does turn up, one of the two models is wrong and it needs decoding, not typing.
		public byte[]? Scale { get; set; } // null when absent

		public ulong Playfield { get; set; }

		// The client's own SimpleCharFullUpdateIIR scaffold between the transform and the embedded CoreCharacter.
		public SpawnPreIir PreIir { get; set; } = new();

		// The head of the embedded CoreCharacterFullUpdateIIR: its version byte and the type-stable
		// field-presence/shape word that follows it. Every captured player frame carries 0x8208 (one carries
		// 0x820A); an NPC's comes from NpcStatCore.IirHeader.
		public byte CoreCharVersion { get; set; } = SimpleCharFullUpdateCodec.CoreCharacterVersion;
		public uint CoreCharIirHeader { get; set; } = DefaultCoreCharIirHeader;

		// The embedded character message - identity, appearance, equipment. TYPED, not bytes: its re-encode is
		// byte-identical on every captured spawn (ProtobufCorpusRoundTripTests).
		public ProtoCoreCharacterIIR Character { get; set; } = new();

		public SpawnPostIir Post { get; set; } = new();

		public const uint DefaultCoreCharIirHeader = 0x8208;
	}
}

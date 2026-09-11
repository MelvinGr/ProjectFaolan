using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     SimpleCharFullUpdateIIR - the entity spawn body, for a player and for an NPC alike:
	///     <code>
	///     [u32 subop][u32 class][u32 handle][u32 fieldA]
	///     [varint transformLen][0x0A vec3][0x12 quat][opt 0x25 scale][0x18 playfield varint]
	///     [pre-IIR scaffold]                                     SpawnPreIirCodec
	///     [u8 0x41][u32 coreCharIirHeader][u32 msgLen][msg]      the embedded CoreCharacterFullUpdateIIR
	///     [post-IIR stream]                                      SpawnPostIirCodec
	///     </code>
	/// </summary>
	/// <remarks>
	///     The two families differ in one region, the post-IIR entry slot; see <see cref="SpawnPostIir" />. The u32 carrier
	///     length belongs to the framing sender, so this builds the record only.
	/// </remarks>
	public static class SimpleCharFullUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleCharFullUpdateIIR;

		/// <summary>The embedded CoreCharacterFullUpdateIIR's version byte, 'A'. The client rejects any other value.</summary>
		public const byte CoreCharacterVersion = 0x41;

		/// <summary>
		///     The transform component's protobuf field tags: a ProtoGameCodeDynelIIR with position 1, rotation 2 and
		///     playfield_instance 3. All 15,787 captured spawn and dynel groups carry exactly those three fields in that
		///     order; field 4, a fixed32, is never observed - see the Scale remark on the value type.
		/// </summary>
		private const byte PositionTag = 0x0A;
		private const byte RotationTag = 0x12;
		private const byte ScaleTag = 0x25;
		private const byte PlayfieldTag = 0x18;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

			// The post-IIR runs to the end of the record, and its grammar is end-anchored; see SpawnPostIirCodec.
			value.Post = SpawnPostIirCodec.Read(r.ReadRemaining());
			return value;
		}

		internal static void Write(FaolanStream s, SimpleCharFullUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.Ack).WriteUInt32(v.Handle).WriteUInt32(v.FieldA);

			// The transform-length prefix must be computed, never hardcoded: the client reads exactly this many bytes
			// as the base transform and then reads the version byte, so it has to equal the actual transform length.
			// It is 44 for the usual 4-byte playfield-token varint - every captured token falls in the range protobuf
			// writes as four bytes - but a zero or small token encodes shorter, and a fixed 44 then over-reads by three
			// and the client asserts.
			using var transform = new FaolanStream();
			transform.WriteByte(PositionTag).WriteProtobufByteLength((ProtoVec3)v.Position).WriteByte(RotationTag).WriteProtobufByteLength((ProtoQuat)v.Rotation);
			if (v.Scale != null)
				transform.WriteByte(ScaleTag).WriteArray(v.Scale);

			transform.WriteByte(PlayfieldTag).WriteVarint(v.Playfield);

			var transformBytes = transform.ToArray();
			s.WriteVarint((ulong)transformBytes.Length).WriteArray(transformBytes);

			SpawnPreIirCodec.Write(s, v.PreIir);

			// A u32 length, not a byte one: the CharacterIIR is [u32 msgLen][protobuf], and every real character's
			// message is longer than 255 bytes, so the byte-length helper throws on everything except a fixture small
			// enough to hide it.
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

	/// <summary>
	///     The SimpleCharFullUpdateIIR body - a whole entity spawn, per the validated grammar in
	///     docs/re/player-spawn-generation.md and docs/re/fa015807-spawn-reader.md.
	/// </summary>
	public sealed record SimpleCharFullUpdateIIR
	{
		public uint Ack { get; set; } = EntityClass.Character;
		public uint Handle { get; set; }
		public uint FieldA { get; set; }

		/// <summary>
		///     The transform component. Typed rather than kept as raw field payloads: measured over 11,632 captured player
		///     spawns the field set never varies - the vec3 is always {0D,15,1D} and the quaternion always {0D,15,1D,25} -
		///     and the byte-exact regeneration gate over every captured spawn confirms nothing is lost.
		/// </summary>
		public Vector3 Position { get; set; }
		public Quaternion Rotation { get; set; }

		/// <summary>
		///     Field 4 as a fixed32, kept raw because it is genuinely unmodelled: it is absent from all 11,632 captured
		///     player spawns, so the corpus proves nothing about what it would contain, and ProtoGameCodeDynelIIR declares
		///     field 4 as a length-delimited repeated RunningEffects rather than a fixed32. If one ever does turn up, one of
		///     the two models is wrong and it needs decoding rather than typing. Null when absent.
		/// </summary>
		public byte[]? Scale { get; set; }

		public ulong Playfield { get; set; }

		/// <summary>
		///     The client's own SimpleCharFullUpdateIIR scaffold, between the transform and the embedded CoreCharacter.
		/// </summary>
		public SpawnPreIir PreIir { get; set; } = new SpawnPreIir();

		/// <summary>
		///     The head of the embedded CoreCharacterFullUpdateIIR: its version byte and the type-stable field-presence and
		///     shape word that follows it. Every captured player frame carries 0x8208, one carrying 0x820A; an NPC's comes
		///     from NpcStatCore.IirHeader.
		/// </summary>
		public byte CoreCharVersion { get; set; } = SimpleCharFullUpdateCodec.CoreCharacterVersion;
		public uint CoreCharIirHeader { get; set; } = DefaultCoreCharIirHeader;

		/// <summary>
		///     The embedded character message - identity, appearance, equipment. Typed rather than bytes: its re-encode is
		///     byte-identical on every captured spawn (ProtobufCorpusRoundTripTests).
		/// </summary>
		public ProtoCoreCharacterIIR Character { get; set; } = new ProtoCoreCharacterIIR();

		public SpawnPostIir Post { get; set; } = new SpawnPostIir();

		public const uint DefaultCoreCharIirHeader = 0x8208;
	}
}

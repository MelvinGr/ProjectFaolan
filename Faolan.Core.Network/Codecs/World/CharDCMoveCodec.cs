using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// CharDCMoveIIR_t (0x4517AA81) - the dead-reckoning move (#190, docs/re/chardcmove-grammar.md): the per-tick
	// movement broadcast an entity streams, complementing MoveTaskCodec (the path/transform form). A leading bitfield
	// (Flag1) gates which optional 3-float vectors are present (FUN_007ad6b0), so Build/Parse preserve every field
	// and the record round-trips byte-exact (DeadReckoningCodecTests).
	//
	// Body (the u32 length prefix belongs to the SENDER, #530 rule 2 - WorldIir.Send writes it):
	//   [subop][0x0000C350][handle][u8 env][u8 flag1][pos 3f][rotation 2 or 4f][u8 flag2][ move block ]
	//
	// ⚠️ The move block carries an extra u16 when `flag2 == 1`, and ONLY then (#811). The client's reader says so
	// outright - after `param16` it does
	//     if (flag2 == 1) <read u16 into this+0x4a>;  else  this+0x4a = 0xFFFF;
	// so the field is not optional data but a different record SHAPE, defaulted rather than absent. It is 58 of the
	// 53,886 captured records - the whole `flag2 == 1` population, exactly - and reading past it shifted every field
	// after `param16` by two bytes, which is why the codec reported "2 unread bytes" at the END rather than a wrong
	// heading: `EnsureAtEnd` is what caught it, and only because the record happens to end short.
	public static class CharDCMoveCodec
	{
		public const uint Opcode = (uint)IirTypes.CharDCMoveIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharDCMoveIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// The dominant 0x1e form - a stationary-ish update carrying only position + facing (no velocity/accel/heading
		// vectors). `rotation` is the entity's orientation; in this collapsed form only its (Y, W) reach the wire,
		// which is lossless for the upright yaw-only facings every character uses. `param19` is the per-tick scalar
		// the client interpolates with (0 = none).
		public static byte[] Build(uint handle, Vector3 position, Quaternion rotation, float param19 = 0f)
		{
			return Build(new CharDCMoveIIR
			{
				Handle = handle,
				Env = 0x01,
				Flag1 = 0x1e, // bit1|bit2|bit3|bit4 -> collapsed rotation, no optional vectors
				Position = position,
				Rotation = rotation,
				Flag2 = 0x07,
				Param16 = 0f,
				Param19 = param19
			});
		}

		public static CharDCMoveIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(CharDCMoveCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static CharDCMoveIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "CharDCMoveIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var value = new CharDCMoveIIR
			{
				Handle = r.ReadU32Be(),
				Env = r.ReadU8(),
				Flag1 = r.ReadU8()
			};
			value.Position = r.ReadVector3Be();

			// bit4 set: only (qy, qw) are on the wire (qx = qz = 0); clear: the whole quaternion, x/y/z then w.
			value.Rotation = value.Bit4
				? new Quaternion(0f, r.ReadF32Be(), 0f, r.ReadF32Be())
				: r.ReadQuaternionBe();
			value.Flag2 = r.ReadU8();
			if (value.HasMoveBlock)
			{
				if (!value.Bit2)
					value.Velocity = r.ReadVector3Be();
				if (!value.Bit3)
					value.Acceleration = r.ReadVector3Be();
				value.Param16 = r.ReadF32Be();
				if (value.HasFlag2Word)
					value.Flag2Word = r.ReadU16Be();
				if (!value.Bit1)
					value.Heading = r.ReadVector3Be();
				value.Param19 = r.ReadF32Be();
				value.Bool1 = r.ReadU8();
				value.Bool2 = r.ReadU8();
			}

			return value;
		}

		internal static void Write(FaolanStream s, CharDCMoveIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteByte(v.Flag1).WriteVector3(v.Position);

			// The rotation QUATERNION, collapsed to its yaw-only components when bit4 is set.
			if (v.Bit4)
				s.WriteFloat(v.Rotation.Y);
			else
				s.WriteFloat(v.Rotation.X).WriteFloat(v.Rotation.Y).WriteFloat(v.Rotation.Z);
			s.WriteFloat(v.Rotation.W).WriteByte(v.Flag2);
			if (!v.HasMoveBlock)
				return;

			if (!v.Bit2)
				s.WriteVector3(v.Velocity);
			if (!v.Bit3)
				s.WriteVector3(v.Acceleration);
			s.WriteFloat(v.Param16);
			if (v.HasFlag2Word)
				s.WriteUInt16(v.Flag2Word);
			if (!v.Bit1)
				s.WriteVector3(v.Heading);
			s.WriteFloat(v.Param19).WriteByte(v.Bool1).WriteByte(v.Bool2);
		}
	}

	// CharDCMoveIIR (0x4517AA81).
	public sealed record CharDCMoveIIR
	{
		public Vector3 Acceleration { get; set; } // present when !bit3
		public Vector3 Heading { get; set; } // present when !bit1

		public Vector3 Position { get; set; }

		// The entity's ORIENTATION - the floats after the position, which the decompile's field names ("the
		// post-position move vector", facingX, param6) hid: they are a unit quaternion, and the "collapse" bit4
		// performs is dropping the two components that are zero for an upright character. Measured over the whole
		// corpus: |q| = 1 within 1e-4 for 100% of 19,642 collapsed AND 3,250 full records, and the yaw it implies
		// (2*atan2(qy, qw)) matches the direction of travel within 7.5 deg for 90% of sustained straight runs.
		// The same 4 floats close StopIIR (StopCodec).
		public Quaternion Rotation { get; set; }

		// the velocity/accel/heading block, present only when Flag2 < 8:
		public Vector3 Velocity { get; set; } // present when !bit2
		public byte Bool1 { get; set; }
		public byte Bool2 { get; set; }
		public byte Env { get; set; }
		public byte Flag1 { get; set; }
		public byte Flag2 { get; set; }

		// The u16 that rides between Param16 and the heading when Flag2 == 1, and the client's own default for it
		// when the field is absent. Its 58 captured values are 0x0003 (x26), 0x0008 (x6) and 0x0009 (x26); what it
		// selects is undecoded [V0].
		public const ushort NoFlag2Word = 0xFFFF;

		public ushort Flag2Word { get; set; } = NoFlag2Word;
		public float Param16 { get; set; }
		public float Param19 { get; set; }
		public uint Handle { get; set; }

		public bool Bit1 => (Flag1 & 0x02) != 0;
		public bool Bit2 => (Flag1 & 0x04) != 0;
		public bool Bit3 => (Flag1 & 0x08) != 0;
		public bool Bit4 => (Flag1 & 0x10) != 0;
		public bool HasMoveBlock => Flag2 < 8;

		public bool HasFlag2Word => Flag2 == 1;
	}
}

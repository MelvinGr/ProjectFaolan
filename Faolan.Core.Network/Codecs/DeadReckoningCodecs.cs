using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;

namespace Faolan.Core.Network.Codecs
{
	// Construct-side codecs for the dead-reckoning movement records (#190), decoded in docs/chardcmove-grammar.md.
	// These are the per-tick movement broadcasts an entity streams; they complement MoveTaskCodec (the path/transform
	// form). All three share the IIR carrier `[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][reader]` and are
	// big-endian. Build/Parse operate on the full record (length prefix included), mirroring ChatTextCodec; round-trip
	// + construct golden tests in DeadReckoningCodecTests.

	// CharDCMoveIIR_t (0x4517AA81) - the dead-reckoning move. A leading bitfield (flag1) gates which optional 3-float
	// vectors are present (FUN_007ad6b0). Parse/Build preserve every field so the record round-trips byte-exact.
	public sealed class CharDCMove
	{
		public byte Bool1;
		public byte Bool2;
		public byte Env;
		public byte Flag1;
		public byte Flag2;
		public float FacingX; // bit4 set: the collapsed facing (with Param6 = a unit 2D heading)
		public float Param16;
		public float Param19;
		public float Param6;
		public uint Handle;
		public Vector3 Acceleration; // present when !bit3
		public Vector3 Heading; // present when !bit1
		public Vector3 MoveVector; // bit4 clear: the full post-position vector

		public Vector3 Position;

		// the velocity/accel/heading block, present only when Flag2 < 8:
		public Vector3 Velocity; // present when !bit2

		public bool Bit1 => (Flag1 & 0x02) != 0;
		public bool Bit2 => (Flag1 & 0x04) != 0;
		public bool Bit3 => (Flag1 & 0x08) != 0;
		public bool Bit4 => (Flag1 & 0x10) != 0;
		public bool HasMoveBlock => Flag2 < 8;
	}

	public static class CharDCMoveCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000Opcodes.CharDCMoveIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(CharDCMove r)
		{
			var c = new FaolanStream();
			c.WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(r.Handle).WriteByte(r.Env).WriteByte(r.Flag1).WriteVector3(r.Position);
			if (r.Bit4)
				c.WriteFloat(r.FacingX);
			else
				c.WriteVector3(r.MoveVector);
			c.WriteFloat(r.Param6).WriteByte(r.Flag2);
			if (r.HasMoveBlock)
			{
				if (!r.Bit2)
					c.WriteVector3(r.Velocity);
				if (!r.Bit3)
					c.WriteVector3(r.Acceleration);
				c.WriteFloat(r.Param16);
				if (!r.Bit1)
					c.WriteVector3(r.Heading);
				c.WriteFloat(r.Param19).WriteByte(r.Bool1).WriteByte(r.Bool2);
			}

			return WithLength(c.ToArray());
		}

		// (was a local WriteVec3 helper; replaced by FaolanStream.WriteVector3, #278)

		// Convenience: the dominant 0x1e form - a stationary-ish update carrying only position + a 2D facing (no
		// velocity/accel/heading vectors). facingX/facingZ are the unit heading pair (cos/sin); param19 is the
		// per-tick scalar the client interpolates with (0 = none).
		public static byte[] BuildStationaryFacing(uint handle, Vector3 position, float facingX, float facingZ, float param19 = 0f)
		{
			return Build(new CharDCMove
			{
				Handle = handle,
				Env = 0x01,
				Flag1 = 0x1e, // bit1|bit2|bit3|bit4 -> collapsed facing, no optional vectors
				Position = position,
				FacingX = facingX,
				Param6 = facingZ,
				Flag2 = 0x07,
				Param16 = 0f,
				Param19 = param19
			});
		}

		public static CharDCMove Parse(byte[] body)
		{
			var s = new ReadCursor(body);
			s.U32(); // len
			if (s.U32() != SubOpcode)
				throw new InvalidOperationException("not a CharDCMoveIIR record");

			s.U32(); // 0xC350 marker
			var r = new CharDCMove
			{
				Handle = s.U32(),
				Env = s.Byte(),
				Flag1 = s.Byte(),
				Position = s.Vec3()
			};
			if (r.Bit4)
				r.FacingX = s.Float();
			else
				r.MoveVector = s.Vec3();
			r.Param6 = s.Float();
			r.Flag2 = s.Byte();
			if (r.HasMoveBlock)
			{
				if (!r.Bit2)
					r.Velocity = s.Vec3();
				if (!r.Bit3)
					r.Acceleration = s.Vec3();
				r.Param16 = s.Float();
				if (!r.Bit1)
					r.Heading = s.Vec3();
				r.Param19 = s.Float();
				r.Bool1 = s.Byte();
				r.Bool2 = s.Byte();
			}

			return r;
		}

		internal static byte[] WithLength(byte[] body) => new FaolanStream().WriteArrayUInt32Length(body).ToArray();
	}

	// StopIIR_c (0xD751EBB4) - the move-stop record: final position + a 4-float facing/orientation (FUN_00a34700,
	// 7 big-endian floats). The trailing rotation is a unit 2D heading embedded in 4 floats.
	public static class StopCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000Opcodes.StopIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, Vector3 position, float r0, float r1, float r2, float r3, byte env = 0x00)
		{
			var c = new FaolanStream();
			c.WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(handle).WriteByte(env).WriteVector3(position).WriteFloat(r0).WriteFloat(r1).WriteFloat(r2).WriteFloat(r3);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new ReadCursor(body);
			s.U32();
			s.U32();
			s.U32(); // len, subop, marker
			var handle = s.U32();
			var env = s.Byte();
			return new Parsed(handle, env, s.Vec3(), s.Float(), s.Float(), s.Float(), s.Float());
		}

		public sealed record Parsed(uint Handle, byte Env, Vector3 Position, float R0, float R1, float R2, float R3);
	}

	// SetPosIIR_c (0x04B80B24) - the teleport/snap record: position(3) + rotation(4) floats + a trailing bool
	// (FUN_00a33770; the client finite-checks the pos & rot).
	public static class SetPosCodec
	{
		public const uint SubOpcode = (uint)GameServer0X2000Opcodes.SetPosIIR;
		private const uint IirMarker = EntityClass.Character;

		public static byte[] Build(uint handle, Vector3 position, float r0, float r1, float r2, float r3, byte flag = 0x00, byte env = 0x00)
		{
			var c = new FaolanStream();
			c
				.WriteUInt32(SubOpcode)
				.WriteUInt32(IirMarker)
				.WriteUInt32(handle)
				.WriteByte(env)
				.WriteVector3(position)
				.WriteFloat(r0)
				.WriteFloat(r1)
				.WriteFloat(r2)
				.WriteFloat(r3)
				.WriteByte(flag);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new ReadCursor(body);
			s.U32();
			s.U32();
			s.U32();
			var handle = s.U32();
			var env = s.Byte();
			var pos = s.Vec3();
			var (r0, r1, r2, r3) = (s.Float(), s.Float(), s.Float(), s.Float());
			return new Parsed(handle, env, pos, r0, r1, r2, r3, s.Byte());
		}

		public sealed record Parsed(uint Handle, byte Env, Vector3 Position, float R0, float R1, float R2, float R3, byte Flag);
	}

	// A tiny big-endian read cursor over a record body (the protocol is big-endian; floats are raw-body BE).
	[Obsolete("Use FaolanStream")]
	internal ref struct ReadCursor
	{
		private readonly ReadOnlySpan<byte> _b;
		private int _i;

		public ReadCursor(ReadOnlySpan<byte> b)
		{
			_b = b;
			_i = 0;
		}

		public uint U32()
		{
			var v = BinaryPrimitives.ReadUInt32BigEndian(_b.Slice(_i));
			_i += 4;
			return v;
		}

		public float Float()
		{
			var v = BinaryPrimitives.ReadSingleBigEndian(_b.Slice(_i));
			_i += 4;
			return v;
		}

		public byte Byte()
		{
			return _b[_i++];
		}

		public Vector3 Vec3()
		{
			return new Vector3(Float(), Float(), Float());
		}
	}
}

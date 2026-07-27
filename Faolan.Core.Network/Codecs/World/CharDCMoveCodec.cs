using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// Construct-side codecs for the dead-reckoning movement records (#190), decoded in docs/re/chardcmove-grammar.md.
	// These are the per-tick movement broadcasts an entity streams; they complement MoveTaskCodec (the path/transform
	// form). All three share the IIR carrier `[u32 len][u32 sub][u32 0xC350][u32 handle][u8 env][reader]` and are
	// big-endian. Build/Parse operate on the full record (length prefix included), mirroring ChatTextCodec; round-trip
	// + construct golden tests in DeadReckoningCodecTests.
	public static class CharDCMoveCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.CharDCMoveIIR;

		public static byte[] Build(CharDCMove r)
		{
			var c = new FaolanStream();
			c.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(r.Handle).WriteByte(r.Env).WriteByte(r.Flag1).WriteVector3(r.Position);
			// The rotation QUATERNION, collapsed to its yaw-only components when bit4 is set (see CharDCMove.Rotation).
			if (r.Bit4)
				c.WriteFloat(r.Rotation.Y);
			else
				c.WriteFloat(r.Rotation.X).WriteFloat(r.Rotation.Y).WriteFloat(r.Rotation.Z);
			c.WriteFloat(r.Rotation.W).WriteByte(r.Flag2);
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

		// Convenience: the dominant 0x1e form - a stationary-ish update carrying only position + facing (no
		// velocity/accel/heading vectors). `rotation` is the entity's orientation; in this collapsed form only its
		// (Y, W) reach the wire, which is lossless for the upright yaw-only facings every character uses. param19 is
		// the per-tick scalar the client interpolates with (0 = none).
		public static byte[] BuildStationaryFacing(uint handle, Vector3 position, Quaternion rotation, float param19 = 0f)
		{
			return Build(new CharDCMove
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

		public static CharDCMove Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32(); // len
			if (s.ReadUInt32() != Opcode)
				throw new InvalidOperationException("not a CharDCMoveIIR record");

			s.ReadUInt32(); // EntityClass.Character
			var r = new CharDCMove
			{
				Handle = s.ReadUInt32(),
				Env = s.ReadByte(),
				Flag1 = s.ReadByte(),
				Position = s.ReadVector3()
			};
			// bit4 set: only (qy, qw) are on the wire (qx = qz = 0); clear: the whole quaternion, x/y/z then w.
			r.Rotation = r.Bit4
				? new Quaternion(0f, s.ReadFloat(), 0f, s.ReadFloat())
				: new Quaternion(s.ReadFloat(), s.ReadFloat(), s.ReadFloat(), s.ReadFloat());
			r.Flag2 = s.ReadByte();
			if (r.HasMoveBlock)
			{
				if (!r.Bit2)
					r.Velocity = s.ReadVector3();
				if (!r.Bit3)
					r.Acceleration = s.ReadVector3();
				r.Param16 = s.ReadFloat();
				if (!r.Bit1)
					r.Heading = s.ReadVector3();
				r.Param19 = s.ReadFloat();
				r.Bool1 = s.ReadByte();
				r.Bool2 = s.ReadByte();
			}

			return r;
		}

		internal static byte[] WithLength(byte[] body)
		{
			return new FaolanStream().WriteArrayUInt32Length(body).ToArray();
		}
	}

	// CharDCMoveIIR_t (0x4517AA81) - the dead-reckoning move. A leading bitfield (flag1) gates which optional 3-float
	// vectors are present (FUN_007ad6b0). Parse/Build preserve every field so the record round-trips byte-exact.
	public sealed class CharDCMove
	{
		public Vector3 Acceleration; // present when !bit3
		public Vector3 Heading; // present when !bit1

		public Vector3 Position;

		// The entity's ORIENTATION - the floats after the position, which the decompile's field names ("the
		// post-position move vector", facingX, param6) hid: they are a unit quaternion, and the "collapse" bit4
		// performs is dropping the two components that are zero for an upright character. Measured over the whole
		// corpus: |q| = 1 within 1e-4 for 100% of 19,642 collapsed AND 3,250 full records, and the yaw it implies
		// (2*atan2(qy, qw)) matches the direction of travel within 7.5 deg for 90% of sustained straight runs.
		// The same 4 floats close StopIIR (StopCodec).
		public Quaternion Rotation;

		// the velocity/accel/heading block, present only when Flag2 < 8:
		public Vector3 Velocity; // present when !bit2
		public byte Bool1;
		public byte Bool2;
		public byte Env;
		public byte Flag1;
		public byte Flag2;
		public float Param16;
		public float Param19;
		public uint Handle;

		public bool Bit1 => (Flag1 & 0x02) != 0;
		public bool Bit2 => (Flag1 & 0x04) != 0;
		public bool Bit3 => (Flag1 & 0x08) != 0;
		public bool Bit4 => (Flag1 & 0x10) != 0;
		public bool HasMoveBlock => Flag2 < 8;
	}
}

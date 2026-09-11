using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     CharDCMoveIIR - the dead-reckoning move: the per-tick movement broadcast an entity streams, complementing
	///     <see cref="MoveTaskCodec" />, the path and transform form. See docs/re/chardcmove-grammar.md.
	/// </summary>
	/// <remarks>
	///     A leading bitfield (Flag1) gates which optional 3-float vectors are present (client reader FUN_007ad6b0), so
	///     Build and Parse preserve every field and the record round-trips byte-exact (DeadReckoningCodecTests). The body
	///     is <c>[subop][class][handle][u8 env][u8 flag1][pos 3f][rotation 2 or 4f][u8 flag2][move block]</c>; the u32
	///     length prefix belongs to the framing sender.
	///     <para>
	///         The move block carries an extra u16 when <c>flag2 == 1</c>, and only then. The client's reader says so
	///         outright - after <c>param16</c> it reads a u16 into this+0x4a for flag2 1 and otherwise defaults that slot to
	///         0xFFFF - so the field is a different record shape rather than optional data. It is 58 of the 53,886 captured
	///         records, exactly the <c>flag2 == 1</c> population, and reading past it shifts every field after
	///         <c>param16</c> by two bytes, which shows up only as unread bytes at the end.
	///     </para>
	/// </remarks>
	public static class CharDCMoveCodec
	{
		public const uint Opcode = (uint)IirTypes.CharDCMoveIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(CharDCMoveIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     The dominant 0x1e form: a near-stationary update carrying only position and facing, with no velocity,
		///     acceleration or heading vectors. <paramref name="rotation" /> is the entity's orientation, of which only (Y,
		///     W) reach the wire in this collapsed form - lossless for the upright yaw-only facings every character uses.
		///     <paramref name="param19" /> is the per-tick scalar the client interpolates with; 0 is none.
		/// </summary>
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

			// bit4 set: only (qy, qw) are on the wire, qx and qz being 0; clear: the whole quaternion, x/y/z then w.
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

			// The rotation quaternion, collapsed to its yaw-only components when bit4 is set.
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

	/// <summary>The CharDCMoveIIR body.</summary>
	public sealed record CharDCMoveIIR
	{
		public Vector3 Acceleration { get; set; } // present when !bit3
		public Vector3 Heading { get; set; } // present when !bit1

		public Vector3 Position { get; set; }

		/// <summary>
		///     The entity's orientation: the floats after the position are a unit quaternion, and what bit4 collapses is the
		///     two components that are zero for an upright character. Measured over the whole corpus, the magnitude is 1
		///     within 1e-4 for all 19,642 collapsed and 3,250 full records, and the yaw it implies (2*atan2(qy, qw)) matches
		///     the direction of travel within 7.5 degrees for 90% of sustained straight runs. The same four floats close
		///     StopIIR (<see cref="StopCodec" />).
		/// </summary>
		public Quaternion Rotation { get; set; }

		// The velocity, acceleration and heading block, present only when Flag2 is below 8.
		public Vector3 Velocity { get; set; } // present when !bit2
		public byte Bool1 { get; set; }
		public byte Bool2 { get; set; }
		public byte Env { get; set; }
		public byte Flag1 { get; set; }
		public byte Flag2 { get; set; }

		/// <summary>
		///     The client's own default for the u16 that rides between Param16 and the heading when Flag2 is 1, used when
		///     the field is absent. Its 58 captured values are 0x0003 (x26), 0x0008 (x6) and 0x0009 (x26); what it selects is
		///     undecoded. [V0]
		/// </summary>
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

using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// StopIIR_c (0xD751EBB4) - the move-stop record: final position + orientation (FUN_00a34700, 7 big-endian floats).
	// The trailing four are the rotation QUATERNION, in full (CharDCMove's bit4 collapse has no counterpart here) -
	// unit-length for 100% of the corpus's 3,540 records, and yaw-only in shape (qx = qz = 0).
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2) - WorldIir.Send writes it.
	public static class StopCodec
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.StopIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(StopIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, Vector3 position, Quaternion rotation, byte env = 0x00)
		{
			return Build(new StopIIR(handle, env, position, rotation));
		}

		public static StopIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(StopCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static StopIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "StopIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			return new StopIIR(handle, env, r.ReadVector3Be(), r.ReadQuaternionBe());
		}

		internal static void Write(FaolanStream s, StopIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteVector3(v.Position).WriteQuaternion(v.Rotation);
		}
	}

	// StopIIR (0xD751EBB4).
	public sealed record StopIIR(uint Handle, byte Env, Vector3 Position, Quaternion Rotation);
}

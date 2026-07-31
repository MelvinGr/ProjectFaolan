using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// SetPosIIR_c (0x04B80B24) - the teleport/snap record: position(3) + rotation(4) floats + a trailing bool
	// (FUN_00a33770; the client finite-checks the pos & rot).
	//
	// The u32 length prefix belongs to the SENDER (#530 rule 2) - WorldIir.Send writes it.
	public static class SetPosCodec
	{
		public const uint Opcode = (uint)IirTypes.SetPosIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SetPosIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint handle, Vector3 position, Quaternion rotation, byte flag = 0x00, byte env = 0x00)
		{
			return Build(new SetPosIIR(handle, env, position, rotation, flag));
		}

		public static SetPosIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetPosCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SetPosIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SetPosIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var position = r.ReadVector3Be();
			var rotation = r.ReadQuaternionBe();
			return new SetPosIIR(handle, env, position, rotation, r.ReadU8());
		}

		internal static void Write(FaolanStream s, SetPosIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteVector3(v.Position).WriteQuaternion(v.Rotation).WriteByte(v.Flag);
		}
	}

	// SetPosIIR (0x04B80B24).
	public sealed record SetPosIIR(uint Handle, byte Env, Vector3 Position, Quaternion Rotation, byte Flag);
}

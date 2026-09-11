using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     AreaFormulaIIR - an area/AoE formula: a set of targets plus a centre. The client reader (FUN_0079e330) reads two
	///     count-framed lists with a u32 between and after them; the first list holds entity refs [class][instance], the
	///     second world positions [x][y][z] as floats. Grammar from the client reader, see docs/re/v1-iir-readers.md.
	/// </summary>
	/// <remarks>The u32 carrier length belongs to the framing sender, not to this body.</remarks>
	public static class AreaFormulaCodec
	{
		public const uint Opcode = (uint)IirTypes.AreaFormulaIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(AreaFormulaIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static AreaFormulaIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AreaFormulaCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static AreaFormulaIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "AreaFormulaIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();

			var refs = new (uint Class, uint Instance)[r.ReadListCount()];
			for (var i = 0; i < refs.Length; i++)
				refs[i] = (r.ReadU32Be(), r.ReadU32Be());

			var fieldA = r.ReadU32Be();

			var positions = new Vector3[r.ReadListCount()];
			for (var i = 0; i < positions.Length; i++)
				positions[i] = r.ReadVector3Be();

			return new AreaFormulaIIR(handle, env, refs, fieldA, positions, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, AreaFormulaIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env);

			s.WriteUInt32(ListFrame.ForCount(v.Refs.Length));
			foreach (var (cls, instance) in v.Refs)
				s.WriteUInt32(cls).WriteUInt32(instance);

			s.WriteUInt32(v.FieldA);

			s.WriteUInt32(ListFrame.ForCount(v.Positions.Length));
			foreach (var p in v.Positions)
				s.WriteVector3(p);

			s.WriteUInt32(v.FieldB);
		}
	}

	/// <summary>The AreaFormulaIIR body.</summary>
	public sealed record AreaFormulaIIR(uint Handle, byte Env, (uint Class, uint Instance)[] Refs, uint FieldA, Vector3[] Positions, uint FieldB);
}

using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Combat
{
	// AreaFormulaIIR_t (0xCE30BE4E). ReadSubClass (FUN_0079e330) reads two 0x3F1-framed lists (the same list
	// invariant as GameActionListIIR: wire count = (N+1) * 0x3F1) with a u32 between and after: the first list is
	// entity refs [class][instance], the second is world positions [x][y][z] floats - an area/AoE formula (a set of
	// targets + a centre). Grammar from the client reader (docs/re/v1-iir-readers.md), byte-exact.
	public record AreaFormulaCodec(uint Handle, byte Env, (uint Class, uint Instance)[] Refs, uint FieldA, Vector3[] Positions, uint FieldB) : ICodec<AreaFormulaCodec>
	{
		public const uint Opcode = (uint)GameServer0X2000Opcodes.AreaFormulaIIR;
		private const uint ListStride = ListFrame.Unit;

		public static AreaFormulaCodec Read(FaolanStream s)
		{
			s.ReadUInt32(); // length
			s.ReadUInt32(); // subop
			s.ReadUInt32(); // 0xC350
			var handle = s.ReadUInt32();
			var env = s.ReadByte();

			var refs = new (uint, uint)[s.ReadUInt32() / ListStride - 1];
			for (var i = 0; i < refs.Length; i++)
				refs[i] = (s.ReadUInt32(), s.ReadUInt32());

			var fieldA = s.ReadUInt32();

			var positions = new Vector3[s.ReadUInt32() / ListStride - 1];
			for (var i = 0; i < positions.Length; i++)
				positions[i] = s.ReadVector3();

			return new AreaFormulaCodec(handle, env, refs, fieldA, positions, s.ReadUInt32());
		}

		public void Write(FaolanStream s)
		{
			s.WriteArrayUInt32Length(body =>
			{
				body.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteByte(Env);

				body.WriteUInt32(((uint)Refs.Length + 1) * ListStride);
				foreach (var (cls, instance) in Refs)
					body.WriteUInt32(cls).WriteUInt32(instance);

				body.WriteUInt32(FieldA);

				body.WriteUInt32(((uint)Positions.Length + 1) * ListStride);
				foreach (var p in Positions)
					body.WriteVector3(p);

				body.WriteUInt32(FieldB);
			});
		}

		public static AreaFormulaCodec Parse(byte[] body)
		{
			return Read(new FaolanStream(body));
		}
	}
}

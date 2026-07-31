using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// MoveTaskIIR (0x96C46740) - how the server tells clients where an entity is and which way it faces. The full
	// recursive grammar is decoded (docs/re/movetask-grammar.md, #53) and every one of the 6225 captured records
	// round-trips byte-exact (MoveTaskCodecCorpusTests), so this codec models the WHOLE record. That merges the former
	// MoveTaskComposite (the recursive codec) with MoveTaskCodec (the semantic builders) - both declared this same
	// opcode, which is the duplicate-emitter signal (#530 rule 5a).
	//
	// Wire body (the inner 0x2000 IIR record; the u32 length prefix belongs to the SENDER, #530 rule 2):
	//   [subop 0x96C46740][0x0000C350][handle][u16 0][from: 3 raw u32][MoveTaskNode]
	//
	// The SEMANTIC forms - a bare position, a rotate, a walk, a patrol, a knockback - are MoveTaskNode VALUES, built
	// by MoveTaskNodes next door. They are not overloads here because they are not encoders: several of them share a
	// parameter list (a waypoint list plus a float means a path to one and a patrol to another), and picking which by
	// signature is exactly the ambiguity rule 1 removes.
	public static class MoveTaskCodec
	{
		public const uint Opcode = (uint)IirTypes.MoveTaskIIR;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(MoveTaskIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(value.Handle).WriteUInt16(0).WriteUInt32(value.FromX).WriteUInt32(value.FromY).WriteUInt32(value.FromZ);
			MoveTaskNodeCodec.Write(s, value.Task);
			return s.ToArray();
		}

		// Parse the inner record (starting at the subop), throwing on any structure the grammar doesn't cover so an
		// unrecognised form is never silently mis-encoded.
		public static MoveTaskIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MoveTaskCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static MoveTaskIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "MoveTaskIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU16Be(0, "framing word");
			var fromX = r.ReadU32Be();
			var fromY = r.ReadU32Be();
			var fromZ = r.ReadU32Be();
			return new MoveTaskIIR(handle, fromX, fromY, fromZ, MoveTaskNodeCodec.Read(ref r));
		}
	}

	// MoveTaskIIR (0x96C46740). The FROM position is kept as raw u32 bits so a captured float round-trips exactly.
	public sealed record MoveTaskIIR(uint Handle, uint FromX, uint FromY, uint FromZ, MoveTaskNode Task)
	{
		public MoveTaskIIR(uint handle, Vector3 from, MoveTaskNode task)
			: this(handle, BitConverter.SingleToUInt32Bits(from.X), BitConverter.SingleToUInt32Bits(from.Y), BitConverter.SingleToUInt32Bits(from.Z), task)
		{
		}
	}
}

using System;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// Full recursive MoveTaskIIR (0x96C46740) model + codec: parse ANY captured MoveTask record and re-emit it
	// byte-exact (round-trip proven over all 6225 captured records, MoveTaskCompositeTests). The grammar is decoded
	// 100% (docs/re/movetask-grammar.md, #53): the record is [u16 0][from][factory], the factory is [u8 mode][fields],
	// and ListMoveTask (mode 4) nests a count of further factories. The typed builders (MoveTaskCodec.Build*) emit
	// the common forms from semantic params; this codec covers EVERY form (incl. the recursive ListMoveTask) so the
	// movement records can be CONSTRUCTED rather than replayed verbatim (#190/#234/#193). 4-byte fields are kept as
	// raw u32 bits so float values round-trip exactly.
	public sealed class MoveTaskComposite : ICodec<MoveTaskComposite>
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.MoveTaskIIR;

		public MoveTaskNode Task { get; set; } = new MoveTaskNode();

		public uint FromX { get; set; }
		public uint FromY { get; set; }
		public uint FromZ { get; set; }

		public uint Handle { get; set; }

		public static MoveTaskComposite Read(FaolanStream r)
		{
			var subop = r.ReadUInt32();
			if (subop != (uint)GameServer0X2000Opcodes.MoveTaskIIR)
				throw new InvalidOperationException($"MoveTask subop 0x{subop:X} != 0x{(uint)GameServer0X2000Opcodes.MoveTaskIIR:X}");

			r.ReadUInt32(); // type 0xC350
			var c = new MoveTaskComposite
			{
				Handle = r.ReadUInt32()
			};
			if (r.ReadUInt16() != 0)
				throw new InvalidOperationException("MoveTask framing u16 != 0");

			c.FromX = r.ReadUInt32();
			c.FromY = r.ReadUInt32();
			c.FromZ = r.ReadUInt32();
			c.Task = MoveTaskNode.Read(r);
			return c;
		}

		public void Write(FaolanStream s)
		{
			s.WriteUInt32(GameServer0X2000Opcodes.MoveTaskIIR).WriteUInt32(EntityClass.Character).WriteUInt32(Handle).WriteUInt16(0).WriteUInt32(FromX).WriteUInt32(FromY).WriteUInt32(FromZ);
			Task.Write(s);
		}

		public byte[] Encode()
		{
			return this.ToArray();
		}

		// Parse the inner record (starting at the subop, the shape the MoveTaskCodec builders emit), throwing on any
		// structure the grammar doesn't cover so an unrecognised form is never silently mis-encoded.
		public static MoveTaskComposite Parse(byte[] record)
		{
			ArgumentNullException.ThrowIfNull(record);
			using var r = new FaolanStream(record, false);
			var c = Read(r);
			if (r.Position != record.Length)
				throw new InvalidOperationException($"MoveTask parsed {r.Position} of {record.Length} bytes");

			return c;
		}
	}
}

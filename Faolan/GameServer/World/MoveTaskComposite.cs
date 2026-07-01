using System;
using System.Collections.Generic;
using Faolan.Core;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets.Opcodes;

namespace Faolan.GameServer.World
{
	// Full recursive MoveTaskIIR (0x96C46740) model + codec: parse ANY captured MoveTask record and re-emit it
	// byte-exact (round-trip proven over all 6225 captured records, MoveTaskCompositeTests). The grammar is decoded
	// 100% (docs/movetask-grammar.md, #53): the record is [u16 0][from][factory], the factory is [u8 mode][fields],
	// and ListMoveTask (mode 4) nests a count of further factories. The typed builders (MoveTaskCodec.Build*) emit
	// the common forms from semantic params; this codec covers EVERY form (incl. the recursive ListMoveTask) so the
	// movement records can be CONSTRUCTED rather than replayed verbatim (#190/#234/#193). 4-byte fields are kept as
	// raw u32 bits so float values round-trip exactly.
	public sealed class MoveTaskComposite
	{
		public const uint SubOpcode = (uint)GameServer0X2000RespondsOpcodes.MoveTaskIIR; // 0x96C46740
		private const uint WireType = EntityClass.Character; // 0xC350
		public MoveTaskNode Task { get; set; } = new MoveTaskNode();
		public uint FromX { get; set; }
		public uint FromY { get; set; }
		public uint FromZ { get; set; }

		public uint Handle { get; set; }

		public byte[] Encode()
		{
			var s = new FaolanStream().WriteUInt32(SubOpcode).WriteUInt32(WireType).WriteUInt32(Handle).WriteUInt16(0).WriteUInt32(FromX).WriteUInt32(FromY).WriteUInt32(FromZ);
			Task.Write(s);
			return s.ToArray();
		}

		// Parse the inner record (starting at the subop, the shape the MoveTaskCodec builders emit), throwing on any
		// structure the grammar doesn't cover so an unrecognised form is never silently mis-encoded.
		public static MoveTaskComposite Parse(byte[] record)
		{
			ArgumentNullException.ThrowIfNull(record);
			using var r = new FaolanStream(record, false);

			var subop = r.ReadUInt32();
			if (subop != SubOpcode)
				throw new InvalidOperationException($"MoveTask subop 0x{subop:X} != 0x{SubOpcode:X}");

			r.ReadUInt32(); // type 0xC350
			var c = new MoveTaskComposite { Handle = r.ReadUInt32() };
			if (r.ReadUInt16() != 0)
				throw new InvalidOperationException("MoveTask framing u16 != 0");

			c.FromX = r.ReadUInt32();
			c.FromY = r.ReadUInt32();
			c.FromZ = r.ReadUInt32();
			c.Task = MoveTaskNode.Read(r);

			if (r.Position != record.Length)
				throw new InvalidOperationException($"MoveTask parsed {r.Position} of {record.Length} bytes");

			return c;
		}
	}

	// One node of the recursive move-task factory (FUN_00a46800): [u8 mode] + per-mode fields (and, for mode 4,
	// nested child nodes). Fields are kept as raw u32 bits for byte-exact float round-trip.
	public sealed class MoveTaskNode
	{
		// mode 3 Jump / 11 GroundAlign / 5 Ballistic-flag
		public byte Flag { get; set; }

		// WaitMoveTask base header (modes 0, 1, 4, 5): [u8 flags] (+ [u32 dur][u32 rem] when flags&1 == 0).
		public byte HeaderFlags { get; set; }

		// mode 4 ListMoveTask: header + [u8 min] + [u8 count] + children.
		public byte ListMin { get; set; }
		public byte Mode { get; set; }

		// mode 1 PathMoveTask
		public byte PathFlags { get; set; }
		public byte TrailerByte { get; set; } // a3e400 [u8][7 u32]
		public byte[] ApproachTail { get; set; } = new byte[10]; // [u8 1][9 zero bytes]
		public List<MoveTaskNode> Children { get; set; } = [];
		public List<uint> Waypoints { get; set; } = []; // else branch: 3 u32 per waypoint
		public uint ApproachTarget { get; set; }

		// mode 8 approach
		public uint ApproachType { get; set; }
		public uint HeaderDuration { get; set; }
		public uint HeaderRemaining { get; set; }
		public uint? PathFloat10 { get; set; } // present iff PathFlags & 0x10
		public uint? PathFloat20 { get; set; } // present iff PathFlags & 0x20

		// mode 5 Ballistic: header + 7 u32 + flag (Flag).
		public uint[] Ballistic7 { get; set; } = new uint[7];

		// mode 7 Formation: 6 u32, no header.
		public uint[] Formation6 { get; set; } = new uint[6];
		public uint[] TrailerWords { get; set; } = new uint[7];

		// modes 2 Rotate / 10 SetPos: 7 u32 (to3 + quat4); mode 2 adds a trailing u16.
		public uint[] Vec7 { get; set; } = new uint[7];
		public ushort PathPacked1 { get; set; } // PathFlags & 1 branch
		public ushort PathPacked2 { get; set; }
		public ushort RotateTail { get; set; }
		private bool HasHeaderFloats => (HeaderFlags & 1) == 0;

		public void Write(FaolanStream s)
		{
			s.WriteByte(Mode);
			switch (Mode)
			{
				case 0:
					WriteHeader(s);
					break;
				case 1:
					WritePath(s);
					break;
				case 2:
					WriteWords(s, Vec7);
					s.WriteUInt16(RotateTail);
					break;
				case 3:
					s.WriteByte(Flag);
					break;
				case 4:
					WriteHeader(s);
					s.WriteByte(ListMin);
					s.WriteByte((byte)Children.Count);
					foreach (var child in Children)
						child.Write(s);
					break;
				case 5:
					WriteHeader(s);
					WriteWords(s, Ballistic7);
					s.WriteByte(Flag);
					break;
				case 6:
					break;
				case 7:
					WriteWords(s, Formation6);
					break;
				case 8:
					s.WriteUInt32(ApproachType).WriteUInt32(ApproachTarget).WriteArray(ApproachTail);
					break;
				case 10:
					WriteWords(s, Vec7);
					break;
				case 11:
					s.WriteByte(Flag);
					break;
				default:
					throw new InvalidOperationException($"MoveTask: unsupported mode {Mode}");
			}
		}

		private void ReadHeader(FaolanStream r)
		{
			HeaderFlags = r.ReadByte();
			if (HasHeaderFloats)
			{
				HeaderDuration = r.ReadUInt32();
				HeaderRemaining = r.ReadUInt32();
			}
		}

		private void ReadPath(FaolanStream r)
		{
			ReadHeader(r);
			PathFlags = r.ReadByte();
			if ((PathFlags & 0x10) != 0)
				PathFloat10 = r.ReadUInt32();
			if ((PathFlags & 0x20) != 0)
				PathFloat20 = r.ReadUInt32();
			if ((PathFlags & 1) != 0)
			{
				PathPacked1 = r.ReadUInt16();
				PathPacked2 = r.ReadUInt16();
			}
			else
			{
				var count = r.ReadUInt32();
				if (count > 1000)
					throw new InvalidOperationException($"PathMoveTask waypoint count {count}");

				Waypoints = new List<uint>((int)(count * 3));
				for (var i = 0; i < count * 3; i++)
					Waypoints.Add(r.ReadUInt32());
			}

			ReadTrailer(r); // a3e400 [u8][7 u32]
		}

		private void ReadTrailer(FaolanStream r)
		{
			TrailerByte = r.ReadByte();
			ReadWords(r, TrailerWords);
		}

		private void WriteHeader(FaolanStream s)
		{
			s.WriteByte(HeaderFlags);
			if (HasHeaderFloats)
				s.WriteUInt32(HeaderDuration).WriteUInt32(HeaderRemaining);
		}

		private void WritePath(FaolanStream s)
		{
			WriteHeader(s);
			s.WriteByte(PathFlags);
			if ((PathFlags & 0x10) != 0)
				s.WriteUInt32(PathFloat10!.Value);
			if ((PathFlags & 0x20) != 0)
				s.WriteUInt32(PathFloat20!.Value);
			if ((PathFlags & 1) != 0)
				s.WriteUInt16(PathPacked1).WriteUInt16(PathPacked2);
			else
			{
				s.WriteUInt32((uint)(Waypoints.Count / 3));
				foreach (var w in Waypoints)
					s.WriteUInt32(w);
			}

			WriteTrailer(s);
		}

		private void WriteTrailer(FaolanStream s)
		{
			s.WriteByte(TrailerByte);
			WriteWords(s, TrailerWords);
		}

		public static MoveTaskNode Read(FaolanStream r)
		{
			var n = new MoveTaskNode { Mode = r.ReadByte() };
			switch (n.Mode)
			{
				case 0:
					n.ReadHeader(r);
					break;
				case 1:
					n.ReadPath(r);
					break;
				case 2:
					ReadWords(r, n.Vec7);
					n.RotateTail = r.ReadUInt16();
					break;
				case 3:
					n.Flag = r.ReadByte();
					break;
				case 4:
					n.ReadHeader(r);
					n.ListMin = r.ReadByte();
					var count = r.ReadByte();
					for (var i = 0; i < count; i++)
						n.Children.Add(Read(r));
					break;
				case 5:
					n.ReadHeader(r);
					ReadWords(r, n.Ballistic7);
					n.Flag = r.ReadByte();
					break;
				case 6:
					break; // AnimatedMoveTask reads nothing
				case 7:
					ReadWords(r, n.Formation6);
					break;
				case 8:
					n.ApproachType = r.ReadUInt32();
					n.ApproachTarget = r.ReadUInt32();
					n.ApproachTail = r.ReadArray(10);
					break;
				case 10:
					ReadWords(r, n.Vec7);
					break;
				case 11:
					n.Flag = r.ReadByte();
					break;
				default:
					throw new InvalidOperationException($"MoveTask: unsupported mode {n.Mode}");
			}

			return n;
		}

		private static void ReadWords(FaolanStream r, uint[] words)
		{
			for (var i = 0; i < words.Length; i++)
				words[i] = r.ReadUInt32();
		}

		private static void WriteWords(FaolanStream s, uint[] words)
		{
			foreach (var w in words)
				s.WriteUInt32(w);
		}
	}
}

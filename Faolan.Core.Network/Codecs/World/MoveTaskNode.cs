using System.Collections.Generic;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     One node of the recursive MoveTask tree. A plain value that does not serialize itself;
	///     <see cref="MoveTaskNodeCodec" /> below owns the reading and the writing.
	/// </summary>
	public sealed class MoveTaskNode
	{
		public List<MoveTaskNode> Children { get; set; } = [];

		public List<uint> Waypoints { get; set; } = []; // else branch: 3 u32 per waypoint

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
		/// <summary>
		///     With bit 0 of <see cref="PathFlags" /> set, the roam route is sent by reference rather than by value: instead
		///     of an explicit waypoint list, these two u16 index the playfield's territory table (RDB 1010506,
		///     GameData::PlayfieldTerritory_c) which the client already holds. PathMoveTask::InitRoamPath resolves
		///     <c>territories[TerritoryIndex].paths[TerritoryPathIndex]</c> and walks it, bounds-checking both. The rest of
		///     the plan - anchor, speed, duration, remaining - is sent either way, so the two forms are interchangeable
		///     encodings of one movement plan and every client reproduces the same route.
		/// </summary>
		public ushort TerritoryIndex { get; set; }

		/// <summary>
		///     The index of the wander path within that territory. 0xFFFF means no roam path, which is the client's own
		///     guard value.
		/// </summary>
		public ushort TerritoryPathIndex { get; set; }
		public ushort RotateTail { get; set; }

		internal bool HasHeaderFloats => (HeaderFlags & 1) == 0;
	}

	/// <summary>
	///     The MoveTask node grammar (docs/re/movetask-grammar.md), fully decoded: every one of the 6,225 captured records
	///     round-trips byte-exact (MoveTaskCodecCorpusTests).
	/// </summary>
	/// <remarks>
	///     A sub-record codec, only ever composed - by <see cref="MoveTaskCodec" /> for the MoveTaskIIR packet and by the
	///     spawn post-IIR's move-task slot - so it declares no opcode and exposes only the internal read/write pair.
	///     Four-byte fields are kept as raw u32 bits rather than floats so float values round-trip exactly.
	/// </remarks>
	public static class MoveTaskNodeCodec
	{
		internal static MoveTaskNode Read(ref SpanReader r)
		{
			var n = new MoveTaskNode
			{
				Mode = r.ReadU8()
			};
			switch (n.Mode)
			{
				case 0:
				{
					ReadHeader(ref r, n);
					break;
				}

				case 1:
				{
					ReadPath(ref r, n);
					break;
				}

				case 2:
				{
					ReadWords(ref r, n.Vec7);
					n.RotateTail = r.ReadU16Be();
					break;
				}

				case 3:
				{
					n.Flag = r.ReadU8();
					break;
				}

				case 4:
				{
					ReadHeader(ref r, n);
					n.ListMin = r.ReadU8();
					var count = r.ReadU8();
					for (var i = 0; i < count; i++)
						n.Children.Add(Read(ref r));
					break;
				}

				case 5:
				{
					ReadHeader(ref r, n);
					ReadWords(ref r, n.Ballistic7);
					n.Flag = r.ReadU8();
					break;
				}

				case 6:
				{
					break; // AnimatedMoveTask reads nothing
				}

				case 7:
				{
					ReadWords(ref r, n.Formation6);
					break;
				}

				case 8:
				{
					n.ApproachType = r.ReadU32Be();
					n.ApproachTarget = r.ReadU32Be();
					n.ApproachTail = r.ReadBytes(10).ToArray();
					break;
				}

				case 10:
				{
					ReadWords(ref r, n.Vec7);
					break;
				}

				case 11:
				{
					n.Flag = r.ReadU8();
					break;
				}

				default:
				{
					throw CodecParseException.Unexpected(nameof(MoveTaskNodeCodec), r.Position - 1, "a modelled MoveTask mode", n.Mode.ToString());
				}
			}

			return n;
		}

		internal static void Write(FaolanStream s, MoveTaskNode n)
		{
			s.WriteByte(n.Mode);
			switch (n.Mode)
			{
				case 0:
				{
					WriteHeader(s, n);
					break;
				}

				case 1:
				{
					WritePath(s, n);
					break;
				}

				case 2:
				{
					WriteWords(s, n.Vec7);
					s.WriteUInt16(n.RotateTail);
					break;
				}

				case 3:
				{
					s.WriteByte(n.Flag);
					break;
				}

				case 4:
				{
					WriteHeader(s, n);
					s.WriteByte(n.ListMin);
					s.WriteByte(n.Children.Count);
					foreach (var child in n.Children)
						Write(s, child);
					break;
				}

				case 5:
				{
					WriteHeader(s, n);
					WriteWords(s, n.Ballistic7);
					s.WriteByte(n.Flag);
					break;
				}

				case 6:
				{
					break;
				}

				case 7:
				{
					WriteWords(s, n.Formation6);
					break;
				}

				case 8:
				{
					s.WriteUInt32(n.ApproachType).WriteUInt32(n.ApproachTarget).WriteArray(n.ApproachTail);
					break;
				}

				case 10:
				{
					WriteWords(s, n.Vec7);
					break;
				}

				case 11:
				{
					s.WriteByte(n.Flag);
					break;
				}

				default:
				{
					throw new CodecParseException($"{nameof(MoveTaskNodeCodec)}: unsupported MoveTask mode {n.Mode}");
				}
			}
		}

		private static void ReadHeader(ref SpanReader r, MoveTaskNode n)
		{
			n.HeaderFlags = r.ReadU8();
			if (n.HasHeaderFloats)
			{
				n.HeaderDuration = r.ReadU32Be();
				n.HeaderRemaining = r.ReadU32Be();
			}
		}

		private static void ReadPath(ref SpanReader r, MoveTaskNode n)
		{
			ReadHeader(ref r, n);
			n.PathFlags = r.ReadU8();
			if ((n.PathFlags & 0x10) != 0)
				n.PathFloat10 = r.ReadU32Be();
			if ((n.PathFlags & 0x20) != 0)
				n.PathFloat20 = r.ReadU32Be();
			if ((n.PathFlags & 1) != 0)
			{
				n.TerritoryIndex = r.ReadU16Be();
				n.TerritoryPathIndex = r.ReadU16Be();
			}
			else
			{
				var count = r.ReadU32Be();
				if (count > 1000)
					throw CodecParseException.Unexpected(nameof(MoveTaskNodeCodec), r.Position - 4, "a PathMoveTask waypoint count <= 1000", count.ToString());

				n.Waypoints = new List<uint>((int)(count * 3));
				for (var i = 0; i < count * 3; i++)
					n.Waypoints.Add(r.ReadU32Be());
			}

			// a3e400 [u8][7 u32]
			n.TrailerByte = r.ReadU8();
			ReadWords(ref r, n.TrailerWords);
		}

		private static void WriteHeader(FaolanStream s, MoveTaskNode n)
		{
			s.WriteByte(n.HeaderFlags);
			if (n.HasHeaderFloats)
				s.WriteUInt32(n.HeaderDuration).WriteUInt32(n.HeaderRemaining);
		}

		private static void WritePath(FaolanStream s, MoveTaskNode n)
		{
			WriteHeader(s, n);
			s.WriteByte(n.PathFlags);
			if ((n.PathFlags & 0x10) != 0)
				s.WriteUInt32(n.PathFloat10!.Value);
			if ((n.PathFlags & 0x20) != 0)
				s.WriteUInt32(n.PathFloat20!.Value);
			if ((n.PathFlags & 1) != 0)
				s.WriteUInt16(n.TerritoryIndex).WriteUInt16(n.TerritoryPathIndex);
			else
			{
				s.WriteUInt32(n.Waypoints.Count / 3);
				foreach (var w in n.Waypoints)
					s.WriteUInt32(w);
			}

			s.WriteByte(n.TrailerByte);
			WriteWords(s, n.TrailerWords);
		}

		private static void ReadWords(ref SpanReader r, uint[] words)
		{
			for (var i = 0; i < words.Length; i++)
				words[i] = r.ReadU32Be();
		}

		private static void WriteWords(FaolanStream s, uint[] words)
		{
			foreach (var w in words)
				s.WriteUInt32(w);
		}
	}
}

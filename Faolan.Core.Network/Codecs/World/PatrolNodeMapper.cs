using System;
using System.Buffers.Binary;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.World
{
	// Reversible mapping between the wire MoveTaskNode AST (MoveTaskComposite) and the columnarized PatrolNode tree
	// (#316). ToNode decodes a captured task tree into DB rows; ToTask rebuilds the wire node from them. The pair
	// round-trips byte-exact for every committed patrol (PatrolNodeRoundTripTests). Confirmed-float slots
	// (duration/remaining/speed/anchor/waypoints) become float columns; the rest stay raw u32 bits (PathFloat10/20,
	// packed) or ride in WordsData (the fixed numeric arrays that don't columnize).
	public static class PatrolNodeMapper
	{
		// Decodes a wire task node into a PatrolNode row (with Children/Waypoints nav populated recursively). The
		// caller sets PatrolPathId + persists; EF cascades the tree.
		public static PatrolNode ToNode(MoveTaskNode m)
		{
			var n = new PatrolNode
			{
				Mode = m.Mode
			};

			switch (m.Mode)
			{
				case 0: // Wait
					CopyHeaderToNode(m, n);
					break;

				case 1: // Path
					CopyHeaderToNode(m, n);
					n.PathFlags = m.PathFlags;
					if ((m.PathFlags & 0x10) != 0)
						n.PathFloat10 = m.PathFloat10;
					if ((m.PathFlags & 0x20) != 0)
						n.PathFloat20 = m.PathFloat20;
					if ((m.PathFlags & 1) != 0)
					{
						n.PathPacked1 = m.PathPacked1;
						n.PathPacked2 = m.PathPacked2;
					}
					else
					{
						for (var i = 0; i + 2 < m.Waypoints.Count; i += 3)
						{
							n.Waypoints.Add(new PatrolNodeWaypoint
							{
								Ordinal = i / 3,
								PositionX = ToFloat(m.Waypoints[i]),
								PositionY = ToFloat(m.Waypoints[i + 1]),
								PositionZ = ToFloat(m.Waypoints[i + 2])
							});
						}
					}

					n.TrailerByte = m.TrailerByte;
					n.Speed = ToFloat(m.TrailerWords[0]);
					n.AnchorX = ToFloat(m.TrailerWords[4]);
					n.AnchorY = ToFloat(m.TrailerWords[5]);
					n.AnchorZ = ToFloat(m.TrailerWords[6]);
					n.WordsData = PackWords(m.TrailerWords[1], m.TrailerWords[2], m.TrailerWords[3]);
					break;

				case 2: // Rotate
					n.WordsData = PackWords(m.Vec7);
					n.RotateTail = m.RotateTail;
					break;

				case 3: // Jump
				case 11: // GroundAlign
					n.Flag = m.Flag;
					break;

				case 4: // List
					CopyHeaderToNode(m, n);
					n.ListMin = m.ListMin;
					for (var i = 0; i < m.Children.Count; i++)
					{
						var child = ToNode(m.Children[i]);
						child.Ordinal = i;
						n.Children.Add(child);
					}

					break;

				case 5: // Ballistic
					CopyHeaderToNode(m, n);
					n.WordsData = PackWords(m.Ballistic7);
					n.Flag = m.Flag;
					break;

				case 6: // Animated - nothing
					break;

				case 7: // Formation
					n.WordsData = PackWords(m.Formation6);
					break;

				case 8: // Approach
					n.ApproachType = m.ApproachType;
					n.ApproachTarget = m.ApproachTarget;
					n.WordsData = m.ApproachTail;
					break;

				case 10: // SetPos
					n.WordsData = PackWords(m.Vec7);
					break;

				default:
					throw new InvalidOperationException($"PatrolNodeMapper: unsupported MoveTask mode {m.Mode}");
			}

			return n;
		}

		// Rebuilds the wire task node from a PatrolNode row (recursing into Children ordered by Ordinal).
		public static MoveTaskNode ToTask(PatrolNode n)
		{
			var m = new MoveTaskNode
			{
				Mode = n.Mode
			};

			switch (n.Mode)
			{
				case 0:
					CopyHeaderToTask(n, m);
					break;

				case 1:
					CopyHeaderToTask(n, m);
					m.PathFlags = n.PathFlags!.Value;
					if ((m.PathFlags & 0x10) != 0)
						m.PathFloat10 = n.PathFloat10;
					if ((m.PathFlags & 0x20) != 0)
						m.PathFloat20 = n.PathFloat20;
					if ((m.PathFlags & 1) != 0)
					{
						m.PathPacked1 = (ushort)n.PathPacked1!.Value;
						m.PathPacked2 = (ushort)n.PathPacked2!.Value;
					}
					else
					{
						m.Waypoints = [];
						foreach (var w in n.Waypoints.OrderBy(w => w.Ordinal))
						{
							m.Waypoints.Add(ToBits(w.PositionX));
							m.Waypoints.Add(ToBits(w.PositionY));
							m.Waypoints.Add(ToBits(w.PositionZ));
						}
					}

					m.TrailerByte = n.TrailerByte!.Value;
					var mid = UnpackWords(n.WordsData!, 3);
					m.TrailerWords = [ToBits(n.Speed), mid[0], mid[1], mid[2], ToBits(n.AnchorX), ToBits(n.AnchorY), ToBits(n.AnchorZ)];
					break;

				case 2:
					m.Vec7 = UnpackWords(n.WordsData!, 7);
					m.RotateTail = (ushort)n.RotateTail!.Value;
					break;

				case 3:
				case 11:
					m.Flag = n.Flag!.Value;
					break;

				case 4:
					CopyHeaderToTask(n, m);
					m.ListMin = n.ListMin!.Value;
					foreach (var child in n.Children.OrderBy(c => c.Ordinal))
						m.Children.Add(ToTask(child));
					break;

				case 5:
					CopyHeaderToTask(n, m);
					m.Ballistic7 = UnpackWords(n.WordsData!, 7);
					m.Flag = n.Flag!.Value;
					break;

				case 6:
					break;

				case 7:
					m.Formation6 = UnpackWords(n.WordsData!, 6);
					break;

				case 8:
					m.ApproachType = n.ApproachType!.Value;
					m.ApproachTarget = n.ApproachTarget!.Value;
					m.ApproachTail = n.WordsData!;
					break;

				case 10:
					m.Vec7 = UnpackWords(n.WordsData!, 7);
					break;

				default:
					throw new InvalidOperationException($"PatrolNodeMapper: unsupported PatrolNode mode {n.Mode}");
			}

			return m;
		}

		// The WaitMoveTask base header (modes 0/1/4/5): flags, plus duration/remaining floats when flags&1 == 0.
		private static void CopyHeaderToNode(MoveTaskNode m, PatrolNode n)
		{
			n.HeaderFlags = m.HeaderFlags;
			if ((m.HeaderFlags & 1) == 0)
			{
				n.Duration = ToFloat(m.HeaderDuration);
				n.Remaining = ToFloat(m.HeaderRemaining);
			}
		}

		private static void CopyHeaderToTask(PatrolNode n, MoveTaskNode m)
		{
			m.HeaderFlags = n.HeaderFlags!.Value;
			if ((m.HeaderFlags & 1) == 0)
			{
				m.HeaderDuration = ToBits(n.Duration);
				m.HeaderRemaining = ToBits(n.Remaining);
			}
		}

		private static byte[] PackWords(params uint[] words)
		{
			var bytes = new byte[words.Length * 4];
			for (var i = 0; i < words.Length; i++)
				BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), words[i]);
			return bytes;
		}

		private static uint ToBits(float? value)
		{
			return BitConverter.SingleToUInt32Bits(value ?? 0f);
		}

		private static float ToFloat(uint bits)
		{
			return BitConverter.UInt32BitsToSingle(bits);
		}

		private static uint[] UnpackWords(byte[] data, int count)
		{
			var words = new uint[count];
			for (var i = 0; i < count; i++)
				words[i] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i * 4, 4));
			return words;
		}
	}
}

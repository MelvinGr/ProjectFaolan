using System;
using System.Linq;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.World
{
	// Reversible mapping between the wire MoveTaskNode AST (MoveTaskCodec) and the columnarized PatrolNode tree
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
						n.TerritoryIndex = m.TerritoryIndex;
						n.TerritoryPathIndex = m.TerritoryPathIndex;
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
					n.PathTrailerWord = m.TrailerWords[1];
					n.PathDirection1 = ToFloat(m.TrailerWords[2]);
					n.PathDirection2 = ToFloat(m.TrailerWords[3]);
					break;

				case 2: // Rotate
					SetTransform(n, m.Vec7);
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
					SetWords(n, m.Ballistic7);
					n.Flag = m.Flag;
					break;

				case 6: // Animated - nothing
					break;

				case 7: // Formation
					SetWords(n, m.Formation6);
					break;

				case 8: // Approach
					n.ApproachType = m.ApproachType;
					n.ApproachTarget = m.ApproachTarget;
					// The tail is [u8 flag][9 zero bytes]; only the flag is data (#410).
					n.ApproachFlag = m.ApproachTail[0];
					break;

				case 10: // SetPos
					SetTransform(n, m.Vec7);
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
						m.TerritoryIndex = (ushort)n.TerritoryIndex!.Value;
						m.TerritoryPathIndex = (ushort)n.TerritoryPathIndex!.Value;
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
					m.TrailerWords =
					[
						ToBits(n.Speed), n.PathTrailerWord!.Value, ToBits(n.PathDirection1), ToBits(n.PathDirection2),
						ToBits(n.AnchorX), ToBits(n.AnchorY), ToBits(n.AnchorZ)
					];
					break;

				case 2:
					m.Vec7 = Transform(n);
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
					m.Ballistic7 = Words(n, 7);
					m.Flag = n.Flag!.Value;
					break;

				case 6:
					break;

				case 7:
					m.Formation6 = Words(n, 6);
					break;

				case 8:
					m.ApproachType = n.ApproachType!.Value;
					m.ApproachTarget = n.ApproachTarget!.Value;
					m.ApproachTail = new byte[10];
					m.ApproachTail[0] = n.ApproachFlag!.Value;
					break;

				case 10:
					m.Vec7 = Transform(n);
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

		// The Vec7 transform of a Rotate/SetPos node: a position and a unit quaternion (#410).
		private static void SetTransform(PatrolNode n, uint[] vec7)
		{
			n.TargetX = ToFloat(vec7[0]);
			n.TargetY = ToFloat(vec7[1]);
			n.TargetZ = ToFloat(vec7[2]);
			n.TargetRotationX = ToFloat(vec7[3]);
			n.TargetRotationY = ToFloat(vec7[4]);
			n.TargetRotationZ = ToFloat(vec7[5]);
			n.TargetRotationW = ToFloat(vec7[6]);
		}

		private static uint[] Transform(PatrolNode n)
		{
			return
			[
				ToBits(n.TargetX), ToBits(n.TargetY), ToBits(n.TargetZ),
				ToBits(n.TargetRotationX), ToBits(n.TargetRotationY), ToBits(n.TargetRotationZ), ToBits(n.TargetRotationW)
			];
		}

		// The remaining un-named numeric arrays, one PatrolNodeWord row per 32-bit word in wire order (#410).
		private static void SetWords(PatrolNode n, params uint[] words)
		{
			n.Words.Clear();
			for (var i = 0; i < words.Length; i++)
			{
				n.Words.Add(new PatrolNodeWord
				{
					Ordinal = i,
					Value = words[i]
				});
			}
		}

		// Reads them back in order. The count is the MODE's (3 for Path's middle trailer, 7 for Vec7/Ballistic, 6 for
		// Formation), so a row set of the wrong size cannot rebuild the record - fail loudly rather than emit short.
		private static uint[] Words(PatrolNode n, int count)
		{
			var words = n.Words.OrderBy(w => w.Ordinal).Select(w => w.Value).ToArray();
			if (words.Length != count)
				throw new InvalidOperationException($"PatrolNode {n.NpcSpawnId}#{n.OrdinalPath} (mode {n.Mode}) has {words.Length} word row(s), expected {count} - the patrol cannot be rebuilt byte-exact.");

			return words;
		}

		private static uint ToBits(float? value)
		{
			return BitConverter.SingleToUInt32Bits(value ?? 0f);
		}

		private static float ToFloat(uint bits)
		{
			return BitConverter.UInt32BitsToSingle(bits);
		}

	}
}

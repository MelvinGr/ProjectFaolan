using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Decodes AoC's ANIMSYS animation pipeline (#292) so the web viewer can play authentic clips.
	//
	//   1010511 (magic 'LMXB') = clip METADATA tree. Nested key/value tree; all offsets are relative to
	//     BASE = blob+0x08, node = 16 bytes [u32 firstChildOff][u32 attrPtr][u32 nameOff][u16 childCount]
	//     [u16 attrCount], attribute = (u32 nameOff, u32 valueOff) string pairs, ALL values text. The master
	//     1010511:1 is a per-skeleton catalog: `resources` holds `animation` nodes {name, rdbname, duration}.
	//   1010003 = the keyframe CURVES, keyed by the clip's rdbname. RDB-Id header + f32 duration + u32 frameCount,
	//     then per-bone tracks [u32 nameLen][rig_bone_*][T-channel][R-channel]; each channel =
	//     [u32 count][count f32 time][count x (vec3 translation | quat4 rotation)]. Plain float32, 25fps.
	//
	// Full decode + validation write-up in Captures/scripts/decode_lmxb.py (the reference dissector this mirrors).
	public static class AnimationCatalog
	{
		private const uint AnimSysType = 1010511; // LMXB clip metadata
		private const uint CurveType = 1010003; // keyframe curves
		private const uint MasterId = 1; // the 3.6MB master catalog

		// Per-skeleton clip lists are stable for the process; the master parse is ~3.6MB so cache it.
		private static readonly ConcurrentDictionary<uint, IReadOnlyList<ClipInfo>> _clipsBySkeleton = new();

		// Decode one clip's keyframe curves (type 1010003, by rdbname), or null if unavailable / a variant layout.
		public static ClipData? DecodeClip(RdbReader rdb, uint rdbname)
		{
			if (!rdb.TryGet(CurveType, rdbname, out var e) || !rdb.CanRead(e))
				return null;

			var d = rdb.ReadDecompressed(e);
			if (d.Length < 24)
				return null;

			var duration = BitConverter.ToSingle(d, 16);
			var pos = 24;
			var bones = new List<BoneTrack>();
			while (pos + 4 <= d.Length)
			{
				var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos, 4));
				if (nameLen <= 0 || nameLen >= 64 || pos + 4 + nameLen > d.Length || d[pos + 4] != (byte)'r')
					break; // tracks are 'rig_bone_*'; anything else = end (or a variant layout we skip)

				var bone = Encoding.ASCII.GetString(d, pos + 4, nameLen);
				pos += 4 + nameLen;

				if (!TryChannel(d, ref pos, 3, out var posTimes, out var posVals) || !TryChannel(d, ref pos, 4, out var rotTimes, out var rotVals))
					return null; // variant channel layout (a few creature rigs) — bail rather than emit garbage

				bones.Add(new BoneTrack(bone, posTimes, posVals, rotTimes, rotVals));
			}

			return bones.Count == 0
				? null
				: new ClipData(rdbname, duration, bones);
		}

		// The named clips available for a skeleton, sorted by name. Empty when the master catalog or the skeleton
		// entry is unavailable. Deduped by name (the master lists a few clip aliases that share an rdbname).
		public static IReadOnlyList<ClipInfo> ListClips(RdbReader rdb, uint skeletonId)
		{
			return _clipsBySkeleton.GetOrAdd(skeletonId, sk =>
			{
				if (!rdb.TryGet(AnimSysType, MasterId, out var e) || !rdb.CanRead(e))
					return [];

				var lmxb = new Lmxb(rdb.ReadDecompressed(e));
				var skText = sk.ToString();
				foreach (var animsys in lmxb.Children(lmxb.Root()))
				{
					if (lmxb.Attrs(animsys).GetValueOrDefault("skeleton") != skText)
						continue;

					var clips = new List<ClipInfo>();
					var seen = new HashSet<string>();
					foreach (var group in lmxb.Children(animsys))
					{
						foreach (var node in lmxb.Children(group))
						{
							if (lmxb.Name(node) != "animation")
								continue;

							var a = lmxb.Attrs(node);
							if (a.TryGetValue("name", out var name) && a.TryGetValue("rdbname", out var rdbText) && uint.TryParse(rdbText, out var rid) && seen.Add(name))
							{
								float.TryParse(a.GetValueOrDefault("duration"), CultureInfo.InvariantCulture, out var dur);
								clips.Add(new ClipInfo(name, rid, dur));
							}
						}
					}

					return clips.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
				}

				return [];
			});
		}

		// [u32 count][count f32 time][count x width f32 value] -> flat time[] + value[] (count*width). Bounds-checked:
		// false on a count that overruns the buffer (a variant channel layout).
		private static bool TryChannel(byte[] d, ref int p, int width, out float[] times, out float[] values)
		{
			times = [];
			values = [];
			if (p + 4 > d.Length)
				return false;

			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p, 4));
			p += 4;
			if (count < 0 || p + count * 4 * (1 + width) > d.Length)
				return false;

			times = new float[count];
			for (var i = 0; i < count; i++)
				times[i] = BitConverter.ToSingle(d, p + i * 4);
			p += count * 4;

			values = new float[count * width];
			for (var i = 0; i < count * width; i++)
				values[i] = BitConverter.ToSingle(d, p + i * 4);
			p += count * width * 4;
			return true;
		}

		public sealed record ClipInfo(string Name, uint Rdbname, float Duration);

		// One bone's animation: translation (vec3) + rotation (quaternion x,y,z,w) keyframes with their times (seconds).
		public sealed record BoneTrack(string Bone, float[] PosTimes, float[] Pos, float[] RotTimes, float[] Rot);

		public sealed record ClipData(uint Rdbname, float Duration, IReadOnlyList<BoneTrack> Bones);

		// Minimal LMXB clip-metadata tree reader (the 1010511 half of decode_lmxb.py).
		private sealed class Lmxb
		{
			private const int Base = 0x08;
			private readonly byte[] _d;

			public Lmxb(byte[] d)
			{
				_d = d;
			}

			public Dictionary<string, string> Attrs(Node n)
			{
				var outp = new Dictionary<string, string>(StringComparer.Ordinal);
				if (n.AttrPtr == 0xffffffff || n.AttrCount == 0)
					return outp;

				var b = Base + (int)n.AttrPtr;
				for (var i = 0; i < n.AttrCount; i++)
				{
					var nameOff = U32(b + i * 8);
					var valOff = U32(b + i * 8 + 4);
					outp[Str(nameOff)] = Str(valOff);
				}

				return outp;
			}

			public IEnumerable<Node> Children(Node n)
			{
				if (n.ChildOff == 0 || n.ChildCount == 0)
					yield break;

				var cb = Base + (int)n.ChildOff;
				for (var i = 0; i < n.ChildCount; i++)
					yield return NodeAt(cb + i * 0x10);
			}

			public string Name(Node n)
			{
				return Str(n.NameOff);
			}

			public Node Root()
			{
				return NodeAt(Base);
			}

			private Node NodeAt(int off)
			{
				return new Node(U32(off), U32(off + 4), U32(off + 8), U16(off + 0xc), U16(off + 0xe));
			}

			private string Str(uint rel)
			{
				var o = Base + (int)rel;
				var end = Array.IndexOf(_d, (byte)0, o);
				return Encoding.ASCII.GetString(_d, o, (end < 0
					? _d.Length
					: end) - o);
			}

			private ushort U16(int o)
			{
				return BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(o, 2));
			}

			private uint U32(int o)
			{
				return BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(o, 4));
			}

			public readonly record struct Node(uint ChildOff, uint AttrPtr, uint NameOff, ushort ChildCount, ushort AttrCount);
		}
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Decodes AoC's ANIMSYS animation pipeline so the web viewer can play authentic clips. The reference dissector
	///     this mirrors is tools/capture/decode_lmxb.py.
	/// </summary>
	/// <remarks>
	///     Type 1010511 (magic 'LMXB') is the clip metadata tree: a nested key/value tree whose offsets are all
	///     relative to blob + 0x08. A node is 16 bytes — [u32 firstChildOff][u32 attrPtr][u32 nameOff][u16 childCount]
	///     [u16 attrCount] — an attribute is a (u32 nameOff, u32 valueOff) string pair, and every value is text. The
	///     master record 1010511:1 is a per-skeleton catalog whose <c>resources</c> node holds <c>animation</c> nodes
	///     of {name, rdbname, duration}.
	///     <para>
	///         Type 1010003 is the keyframe curves, keyed by the clip's rdbname: an RDB-Id header, an f32 duration and
	///         a u32 frame count, then per-bone tracks [u32 nameLen][rig_bone_*][T-channel][R-channel] where each
	///         channel is [u32 count][count f32 time][count × (vec3 translation | quat4 rotation)]. Values are plain
	///         float32 at 25 fps.
	///     </para>
	/// </remarks>
	public static class AnimationCatalog
	{
		private const uint AnimSysType = 1010511; // LMXB clip metadata
		private const uint CurveType = 1010003; // keyframe curves
		private const uint MasterId = 1; // the 3.6MB master catalog

		/// <summary>
		///     Per-skeleton clip lists, cached because they are stable for the process and the master parse covers
		///     about 3.6 MB.
		/// </summary>
		private static readonly ConcurrentDictionary<uint, IReadOnlyList<ClipInfo>> _clipsBySkeleton = new ConcurrentDictionary<uint, IReadOnlyList<ClipInfo>>();

		/// <summary>
		///     Decodes one clip's keyframe curves (type 1010003, keyed by rdbname).
		/// </summary>
		/// <returns>Null when the record is unavailable or carries a variant layout.</returns>
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
				var nameLen = (int)d.ReadU32Le(pos);
				if (nameLen <= 0 || nameLen >= 64 || pos + 4 + nameLen > d.Length || d[pos + 4] != (byte)'r')
					break; // Tracks are 'rig_bone_*'; anything else ends the list or is a variant layout to skip.

				var bone = Encoding.ASCII.GetString(d, pos + 4, nameLen);
				pos += 4 + nameLen;

				if (!TryChannel(d, ref pos, 3, out var posTimes, out var posVals) || !TryChannel(d, ref pos, 4, out var rotTimes, out var rotVals))
					return null; // A variant channel layout, as a few creature rigs have: bail rather than emit garbage.

				bones.Add(new BoneTrack(bone, posTimes, posVals, rotTimes, rotVals));
			}

			return bones.Count == 0
				? null
				: new ClipData(rdbname, duration, bones);
		}

		/// <summary>
		///     The named clips available for a skeleton, sorted by name and de-duplicated by name, since the master
		///     lists a few clip aliases that share an rdbname. Empty when the master catalog or the skeleton entry is
		///     unavailable.
		/// </summary>
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

		/// <summary>
		///     Reads a [u32 count][count f32 time][count × width f32 value] channel into a flat time array and a flat
		///     value array of count × width entries.
		/// </summary>
		/// <returns>False when the count overruns the buffer, which marks a variant channel layout.</returns>
		private static bool TryChannel(byte[] d, ref int p, int width, out float[] times, out float[] values)
		{
			times = [];
			values = [];
			if (p + 4 > d.Length)
				return false;

			var count = (int)d.ReadU32Le(p);
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

		/// <summary>
		///     One bone's animation: translation (vec3) and rotation (quaternion x, y, z, w) keyframes with their times
		///     in seconds.
		/// </summary>
		public sealed record BoneTrack(string Bone, float[] PosTimes, float[] Pos, float[] RotTimes, float[] Rot);

		public sealed record ClipData(uint Rdbname, float Duration, IReadOnlyList<BoneTrack> Bones);

		/// <summary>
		///     A minimal reader for the LMXB clip-metadata tree, the 1010511 half of the format.
		/// </summary>
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
					var nameOff = _d.ReadU32Le(b + i * 8);
					var valOff = _d.ReadU32Le(b + i * 8 + 4);
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
				return new Node(_d.ReadU32Le(off), _d.ReadU32Le(off + 4), _d.ReadU32Le(off + 8), _d.ReadU16Le(off + 0xc), _d.ReadU16Le(off + 0xe));
			}

			private string Str(uint rel)
			{
				var o = Base + (int)rel;
				var end = Array.IndexOf(_d, (byte)0, o);
				return Encoding.ASCII.GetString(_d, o, (end < 0
					? _d.Length
					: end) - o);
			}

			public readonly record struct Node(uint ChildOff, uint AttrPtr, uint NameOff, ushort ChildCount, ushort AttrCount);
		}
	}
}

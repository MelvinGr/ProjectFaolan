using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// Decodes an Age of Conan RDB skeleton (type 1010002) - the bind-pose bone hierarchy a skinned mesh / ANIMSYS
	// references (#275 Phase 3). Validated against the 71-bone player skeleton (id 14853): composing each bone's local
	// transform down the parent chain yields anatomically-correct world positions (Hips y=1.057, Neck 1.766, Head 1.898).
	//
	// Layout: 12-byte RDB-Id header, u32 version, u32 boneCount, then per bone:
	//   [u32 nameLen][name][float4 quat xyzw][float3 translation][u32 childCount][u32 × childCount child indices]
	// Quaternion + translation are LOCAL (relative to parent); world = parentWorld * local.
	public static class SkeletonReader
	{
		public static IReadOnlyList<Bone> Read(ReadOnlySpan<byte> blob)
		{
			if (blob.Length < 20)
				return [];

			var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(16, 4));
			var bones = new List<Bone>(count);
			var p = 20;
			for (var i = 0; i < count && p + 4 <= blob.Length; i++)
			{
				var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(p, 4));
				p += 4;
				if (nameLen < 0 || p + nameLen + 32 > blob.Length)
					break;

				var name = Encoding.Latin1.GetString(blob.Slice(p, nameLen));
				p += nameLen;

				var q = new Quaternion(F(blob, p), F(blob, p + 4), F(blob, p + 8), F(blob, p + 12));
				p += 16;
				var t = new Vector3(F(blob, p), F(blob, p + 4), F(blob, p + 8));
				p += 12;

				var childCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(p, 4));
				p += 4;
				var children = new int[Math.Max(0, childCount)];
				for (var c = 0; c < children.Length && p + 4 <= blob.Length; c++, p += 4)
					children[c] = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(p, 4));

				bones.Add(new Bone(name, q, t, children));
			}

			ComposeWorld(bones);
			return bones;
		}

		// Resolve parents from the child lists, then compose each bone's world matrix by walking up the parent chain.
		// The walk is iterative with a visited guard: a handful of shipped skeletons have a CYCLIC parent chain (a bone
		// reachable as its own ancestor), which would otherwise recurse forever and StackOverflow (uncatchable) the moment
		// we scan the whole 1010002 corpus to auto-pick a rig.
		private static void ComposeWorld(List<Bone> bones)
		{
			for (var i = 0; i < bones.Count; i++)
			{
				foreach (var c in bones[i].Children)
				{
					if (c >= 0 && c < bones.Count)
						bones[c].Parent = i;
				}
			}

			var seen = new HashSet<int>();
			for (var i = 0; i < bones.Count; i++)
			{
				seen.Clear();
				// world = local_i * local_parent * local_grandparent * ... (row-vector convention).
				var world = Matrix4x4.Identity;
				var j = i;
				while (j >= 0 && j < bones.Count && seen.Add(j))
				{
					var b = bones[j];
					var local = Matrix4x4.CreateFromQuaternion(NormalizeOrIdentity(b.LocalRotation)) * Matrix4x4.CreateTranslation(b.LocalTranslation);
					world *= local;
					j = b.Parent;
				}

				bones[i].World = world;
			}
		}

		private static float F(ReadOnlySpan<byte> b, int off)
		{
			return BinaryPrimitives.ReadSingleLittleEndian(b.Slice(off, 4));
		}

		private static Quaternion NormalizeOrIdentity(Quaternion q)
		{
			return q.LengthSquared() < 1e-12f
				? Quaternion.Identity
				: Quaternion.Normalize(q);
		}

		public sealed record Bone(string Name, Quaternion LocalRotation, Vector3 LocalTranslation, int[] Children)
		{
			public Matrix4x4 World { get; set; } = Matrix4x4.Identity;
			public Vector3 WorldPosition => World.Translation;
			public int Parent { get; set; } = -1;
		}
	}
}

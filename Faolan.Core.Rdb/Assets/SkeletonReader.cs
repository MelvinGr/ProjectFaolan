using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Decodes an Age of Conan RDB skeleton (type 1010002): the bind-pose bone hierarchy a skinned mesh or the
	///     ANIMSYS references. On the 71-bone player skeleton (id 14853), composing each bone's local transform down
	///     the parent chain yields anatomically correct world positions — Hips at y=1.057, Neck at 1.766, Head at
	///     1.898.
	/// </summary>
	/// <remarks>
	///     The layout is a 12-byte RDB-Id header, a u32 version and a u32 bone count, then per bone
	///     <c>[u32 nameLen][name][float4 quat xyzw][float3 translation][u32 childCount][u32 × childCount child
	///     indices]</c>. The quaternion and translation are local, relative to the parent, so
	///     <c>world = parentWorld * local</c>.
	/// </remarks>
	public static class SkeletonReader
	{
		public static IReadOnlyList<Bone> Read(ReadOnlySpan<byte> blob)
		{
			if (blob.Length < 20)
				return [];

			var count = (int)blob.ReadU32Le(16);
			var bones = new List<Bone>(count);
			var p = 20;
			for (var i = 0; i < count && p + 4 <= blob.Length; i++)
			{
				var nameLen = (int)blob.ReadU32Le(p);
				p += 4;
				if (nameLen < 0 || p + nameLen + 32 > blob.Length)
					break;

				var name = Encoding.Latin1.GetString(blob.Slice(p, nameLen));
				p += nameLen;

				var q = new Quaternion(F(blob, p), F(blob, p + 4), F(blob, p + 8), F(blob, p + 12));
				p += 16;
				var t = new Vector3(F(blob, p), F(blob, p + 4), F(blob, p + 8));
				p += 12;

				var childCount = (int)blob.ReadU32Le(p);
				p += 4;
				var children = new int[Math.Max(0, childCount)];
				for (var c = 0; c < children.Length && p + 4 <= blob.Length; c++, p += 4)
					children[c] = (int)blob.ReadU32Le(p);

				bones.Add(new Bone(name, q, t, children));
			}

			ComposeWorld(bones);
			return bones;
		}

		/// <summary>
		///     Resolves parents from the child lists, then composes each bone's world matrix by walking up the parent
		///     chain.
		/// </summary>
		/// <remarks>
		///     The walk is iterative and carries a visited guard because a handful of shipped skeletons have a cyclic
		///     parent chain, with a bone reachable as its own ancestor. A recursive walk would overflow the stack —
		///     uncatchably — as soon as the whole 1010002 corpus is scanned to auto-pick a rig.
		/// </remarks>
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
				// world = local_i * local_parent * local_grandparent * ..., in the row-vector convention.
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
			return b.ReadF32Le(off);
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

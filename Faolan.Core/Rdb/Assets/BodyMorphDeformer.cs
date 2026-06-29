using System;
using System.Collections.Generic;

namespace Faolan.Core.Rdb.Assets
{
	// Body-proportion morphs for the 3D character viewer (#275), reverse-engineered from AgeOfConan.exe
	// ApplyMorph (FUN_00fede10) plus a live gdb capture of the char-create deform-node array
	// (docs/body-morph-re.md, data in docs/morph-capture/body_morphs_aqf.json).
	//
	// AoC body morphs are NOT vertex blend shapes — each slider scales a weighted set of deform JOINTS, and
	// the skinned mesh follows. We approximate the skinning with a radial soft-deform about each joint's
	// captured world position (weighted by the captured joint weight), so the deformation sits at the
	// authentic influence centres with the authentic per-joint falloff instead of guessed regions. (Exact
	// replication = linear-blend skinning with the captured node skeleton + the mesh's BLENDINDICES/WEIGHT;
	// that ties into the #275 rig-import follow-up.) The captured joints are the Aquilonian-female body; other
	// humanoid bodies share the joint topology and are close enough for the viewer.
	public static class BodyMorphDeformer
	{
		// The 7 body-proportion sliders as stored in CharacterBodies (raw editor units, range about [-100,100]).
		public readonly record struct Morphs(float Arm, float Chest, float Bosom, float Stomach, float Arse, float Thigh, float Leg)
		{
			public bool IsNeutral => Arm == 0 && Chest == 0 && Bosom == 0 && Stomach == 0 && Arse == 0 && Thigh == 0 && Leg == 0;
		}

		private readonly record struct Joint((float X, float Y, float Z) Pos, float Weight);
		private readonly record struct MorphDef(float Min, float Max, Joint[] Joints);

		// Captured Aquilonian-female deform-joint data: per morph the CHARACTERCREATOR min/max scale + the
		// weighted joints (world position, influence weight) the slider drives.
		private static readonly Dictionary<string, MorphDef> Defs = new()
		{
			["arm"] = new(-0.0500f, 0.0500f, [new((-0.1425f,1.5423f,-0.0620f),0.800f), new((-0.2276f,1.2656f,-0.0800f),0.600f), new((-0.2536f,1.1391f,-0.0361f),0.400f), new((-0.0178f,1.5859f,-0.0691f),0.800f), new((0.1432f,1.5371f,-0.0924f),0.800f), new((0.2191f,1.2572f,-0.0978f),0.600f), new((0.2478f,1.1272f,-0.0679f),0.400f), new((0.0212f,1.5849f,-0.0750f),0.800f)]),
			["chest"] = new(-0.1000f, 0.1000f, [new((-0.1425f,1.5423f,-0.0620f),0.200f), new((-0.0178f,1.5859f,-0.0691f),0.300f), new((0.1432f,1.5371f,-0.0924f),0.200f), new((0.0212f,1.5849f,-0.0750f),0.300f), new((-0.0006f,1.2712f,-0.0263f),0.600f), new((0.0018f,1.3387f,-0.0323f),0.800f)]),
			["bosom"] = new(-0.1000f, 0.2000f, [new((0.0018f,1.3387f,-0.0323f),0.200f), new((-0.0870f,1.4220f,0.0699f),0.800f), new((0.1206f,1.4170f,0.0386f),0.800f)]),
			["stomach"] = new(-0.1500f, 0.2000f, [new((-0.0016f,1.0528f,0.0004f),0.350f), new((-0.0031f,1.2158f,-0.0146f),0.700f), new((-0.0006f,1.2712f,-0.0263f),0.350f), new((0.0018f,1.3387f,-0.0323f),0.150f)]),
			["arse"] = new(-0.1000f, 0.1000f, [new((-0.0016f,1.0528f,0.0004f),0.800f), new((-0.1176f,1.0761f,-0.0137f),0.200f), new((0.1007f,1.0741f,-0.0569f),0.200f), new((-0.0031f,1.2158f,-0.0146f),0.300f), new((-0.0006f,1.2712f,-0.0263f),0.150f)]),
			["thigh"] = new(-0.0500f, 0.1000f, [new((-0.0016f,1.0528f,0.0004f),0.200f), new((-0.1312f,0.5794f,0.0820f),0.200f), new((-0.1176f,1.0761f,-0.0137f),0.800f), new((0.0948f,0.5683f,-0.0421f),0.200f), new((0.1007f,1.0741f,-0.0569f),0.800f)]),
			["leg"] = new(-0.0350f, 0.1000f, [new((-0.1635f,0.0732f,0.1044f),0.200f), new((-0.1312f,0.5794f,0.0820f),0.800f), new((-0.1176f,1.0761f,-0.0137f),0.500f), new((0.0488f,0.0732f,-0.1449f),0.200f), new((0.0948f,0.5683f,-0.0421f),0.800f), new((0.1007f,1.0741f,-0.0569f),0.500f)]),
		};

		// (slider accessor, morph name) for the 7 body sliders.
		private static readonly (Func<Morphs, float> Value, string Name)[] Sliders =
		[
			(m => m.Arm, "arm"), (m => m.Chest, "chest"), (m => m.Bosom, "bosom"),
			(m => m.Stomach, "stomach"), (m => m.Arse, "arse"), (m => m.Thigh, "thigh"), (m => m.Leg, "leg")
		];

		// A joint's influence radius (model units) and the displacement gain that turns a node scale into the
		// radial vertex displacement the skinning would produce. Tuned against the in-client morphs.
		private const float Radius = 0.18f;
		private const float Gain = 0.8f;

		// Deform the body submesh positions in place. positions = the rendered base mesh's xyz array. No-op if neutral.
		public static void Apply(float[] positions, Morphs morphs)
		{
			if (morphs.IsNeutral)
				return;
			var n = positions.Length / 3;
			if (n == 0)
				return;

			var orig = (float[])positions.Clone();
			var invR2 = 1f / (Radius * Radius);

			foreach (var (value, name) in Sliders)
			{
				var slider = value(morphs);
				if (slider == 0 || !Defs.TryGetValue(name, out var def))
					continue;
				// DB slider [-100,100] -> t -> client node-scale via the captured asymmetric min/max.
				var t = Math.Clamp(slider / 100f, -1f, 1f);
				var strength = (t >= 0 ? t * def.Max : t * -def.Min) * Gain;
				if (strength == 0)
					continue;

				foreach (var j in def.Joints)
				{
					var amp = strength * j.Weight;
					for (var v = 0; v < n; v++)
					{
						var dx = orig[v * 3] - j.Pos.X;
						var dy = orig[v * 3 + 1] - j.Pos.Y;
						var dz = orig[v * 3 + 2] - j.Pos.Z;
						var falloff = MathF.Exp(-(dx * dx + dy * dy + dz * dz) * invR2);
						if (falloff < 0.02f)
							continue;
						// Radial scale about the joint: push verts out (fat) / in (thin) by the node scale.
						var k = amp * falloff;
						positions[v * 3] += dx * k;
						positions[v * 3 + 1] += dy * k;
						positions[v * 3 + 2] += dz * k;
					}
				}
			}
		}
	}
}

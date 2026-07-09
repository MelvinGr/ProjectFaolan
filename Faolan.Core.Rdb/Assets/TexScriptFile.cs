using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	// Parses an Age of Conan RDB TexScript (type 1010035) - the XML node graph that builds a character's final part
	// texture from its base ("originaltexture") via colour operations (#275). The CHARACTERCREATOR's
	// skincolor/haircolor/eyecolor and the per-character CharacterBodyResources reference these by id.
	//
	//   <texscript name="..">
	//     <operation id=1 mesh="-4" name="originaltexture" .../>   <!-- mesh: -3 skin, -4 hair, -5 eyes, -1/-2 body -->
	//     <operation id=3 name="tinting" red=145 green=151 blue=151 .../>      <!-- multiply the base by an RGB -->
	//     <operation id=3 name="projecttexture" textureid=213240 .../>          <!-- overlay/replace with a texture -->
	//     <operation id=2 name="finishedtexture"/>
	//   </texscript>
	public sealed record TexScriptFile(uint Id, string? Name, int MeshTarget, TexScriptFile.Operation Op)
	{
		public const uint Type = 1010035;

		public enum Kind
		{
			None,
			Tint,
			Project
		}

		public static TexScriptFile Parse(uint id, byte[] decompressed)
		{
			var text = Encoding.Latin1.GetString(decompressed);
			var start = text.IndexOf("<texscript", StringComparison.Ordinal);
			var end = text.LastIndexOf('>');
			if (start < 0 || end <= start)
				return new TexScriptFile(id, null, 0, default);

			XElement root;
			try
			{
				root = XElement.Parse(text.Substring(start, end - start + 1));
			}
			catch
			{
				return new TexScriptFile(id, null, 0, default);
			}

			var ops = root.Element("operationlist")?.Elements("operation").ToList() ?? [];
			var meshTarget = ops.Where(o => (string?)o.Attribute("name") == "originaltexture").Select(o => (int?)o.Attribute("mesh")).FirstOrDefault() ?? 0;

			var op = default(Operation);
			foreach (var o in ops)
			{
				var name = (string?)o.Attribute("name");
				if (name == "tinting")
					op = new Operation(Kind.Tint, ByteAttr(o, "red"), ByteAttr(o, "green"), ByteAttr(o, "blue"), 0);
				else if ((name == "projecttexture" || name == "projectselfilluminationmap") && (uint?)o.Attribute("textureid") is { } tid)
				{
					op = new Operation(Kind.Project, 0, 0, 0, tid, FloatAttr(o, "ucoord", 0.5f), FloatAttr(o, "vcoord", 0.5f), FloatAttr(o, "scale", 100f), FloatAttr(o, "rotation", 0f),
						FloatAttr(o, "blendweight", 1f));
				}
			}

			return new TexScriptFile(id, (string?)root.Attribute("name"), meshTarget, op);
		}

		private static byte ByteAttr(XElement el, string name)
		{
			return byte.TryParse((string?)el.Attribute(name), out var v)
				? v
				: (byte)255;
		}

		private static float FloatAttr(XElement el, string name, float fallback)
		{
			return float.TryParse((string?)el.Attribute(name), CultureInfo.InvariantCulture, out var v)
				? v
				: fallback;
		}

		// Tint: R/G/B multiply. Project: overlay TextureId at (U,V) on the target atlas, sized by Scale (percent of the
		// decal's native size), rotated Rotation degrees, alpha-blended by Blend. Placement is meaningful for body decals
		// (mesh -1/-2 tattoos); the eye/skin projects cover the whole part so the placement defaults (full-cover) are unused.
		public readonly record struct Operation(Kind Kind, byte R, byte G, byte B, uint TextureId, float U = 0.5f, float V = 0.5f, float Scale = 100f, float Rotation = 0f, float Blend = 1f);
	}
}

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

		public enum Kind { None, Tint, Project }

		public readonly record struct Operation(Kind Kind, byte R, byte G, byte B, uint TextureId);

		public static TexScriptFile Parse(uint id, byte[] decompressed)
		{
			var text = Encoding.Latin1.GetString(decompressed);
			var start = text.IndexOf("<texscript", System.StringComparison.Ordinal);
			var end = text.LastIndexOf('>');
			if (start < 0 || end <= start)
				return new TexScriptFile(id, null, 0, default);

			XElement root;
			try { root = XElement.Parse(text.Substring(start, end - start + 1)); }
			catch { return new TexScriptFile(id, null, 0, default); }

			var ops = root.Element("operationlist")?.Elements("operation").ToList() ?? [];
			var meshTarget = ops.Where(o => (string?)o.Attribute("name") == "originaltexture")
				.Select(o => (int?)o.Attribute("mesh")).FirstOrDefault() ?? 0;

			var op = default(Operation);
			foreach (var o in ops)
			{
				var name = (string?)o.Attribute("name");
				if (name == "tinting")
					op = new Operation(Kind.Tint, ByteAttr(o, "red"), ByteAttr(o, "green"), ByteAttr(o, "blue"), 0);
				else if ((name == "projecttexture" || name == "projectselfilluminationmap") && (uint?)o.Attribute("textureid") is { } tid)
					op = new Operation(Kind.Project, 0, 0, 0, tid);
			}

			return new TexScriptFile(id, (string?)root.Attribute("name"), meshTarget, op);
		}

		private static byte ByteAttr(XElement el, string name) =>
			byte.TryParse((string?)el.Attribute(name), out var v) ? v : (byte)255;
	}
}

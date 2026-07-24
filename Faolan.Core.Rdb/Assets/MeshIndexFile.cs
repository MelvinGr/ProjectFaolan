using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	// Parses an Age of Conan RDB MESHINDEXFILE record (type 1010030). Each record is a small XML manifest that ties a
	// model together: it points at the actual geometry blob (<RenderMesh file="ID"/> -> RDB type 1010001), an optional
	// collision blob (<CollisionMesh2 file="ID"/>), and a <Materials> list whose <Texture Path="TYPE:ID"/> entries
	// reference FCTX/PNG texture resources. The geometry blob itself is a separate binary (decoded by RenderMeshReader,
	// Gitea #275); this manifest is the plain-XML graph that lets us resolve a model's textures + the geometry id to load.
	//
	// Record layout: 12-byte RDB-Id header (type,id,marker) then the raw XML (no <?xml?> decl in most records, and the
	// file paths carry Windows backslashes inside attribute values, which is valid XML). We parse from the first '<'.
	public sealed record MeshIndexFile(uint Id, string? SourceFile, uint? RenderMeshId, BoundingBox? Bounds, uint? CollisionMeshId, IReadOnlyList<MeshMaterial> Materials)
	{
		// The texture resources referenced anywhere in this model's materials, de-duplicated, in first-seen order.
		public IReadOnlyList<TextureRef> UniqueTextures => Materials.SelectMany(m => m.Textures).Where(t => t.Type != null).GroupBy(t => (t.Type, t.Id)).Select(g => g.First()).ToList();

		// True when the geometry is a skinned character/equipment body (<RenderSkinnedMesh>); its texture is bound by
		// the item/character system, not the mesh manifest (MaterialSets is empty).
		public bool Skinned { get; init; }

		public static MeshIndexFile Parse(uint id, ReadOnlySpan<byte> decompressed)
		{
			// Strip the 12-byte RDB-Id header (if present) and any trailing padding by clipping to the XML span.
			var text = Encoding.Latin1.GetString(decompressed);
			var start = text.IndexOf('<');
			var end = text.LastIndexOf('>');
			if (start < 0 || end <= start)
				return new MeshIndexFile(id, null, null, null, null, []);

			XElement root;
			try
			{
				root = XElement.Parse(text.Substring(start, end - start + 1));
			}
			catch
			{
				return new MeshIndexFile(id, null, null, null, null, []);
			}

			var source = root.Element("Source")?.Attribute("file")?.Value;

			// Static props carry <RenderMesh>; skinned character/equipment bodies carry <RenderSkinnedMesh> (same RDB
			// type 1010001, the vertex declaration just adds BLENDINDICES/BLENDWEIGHT). Geometry decodes the same way.
			var renderMeshEl = root.Element("RenderMesh") ?? root.Element("RenderSkinnedMesh");
			var renderMeshId = ParseUintAttr(renderMeshEl, "file");
			var bounds = BoundingBox.TryParse(renderMeshEl);
			var skinned = renderMeshEl?.Name.LocalName == "RenderSkinnedMesh";

			// Collision is exported as <CollisionMesh2> (newer) or <CollisionMesh>; we only need its id for completeness.
			var collisionEl = root.Element("CollisionMesh2") ?? root.Element("CollisionMesh");
			var collisionId = ParseUintAttr(collisionEl, "file");

			var materials = (root.Element("Materials")?.Elements("Material") ?? []).Select(ParseMaterial).ToList();

			return new MeshIndexFile(id, source, renderMeshId, bounds, collisionId, materials)
			{
				Skinned = skinned
			};
		}

		private static MeshMaterial ParseMaterial(XElement el)
		{
			var name = el.Attribute("Name")?.Value ?? "";
			var textures = el.Elements("Texture").Select(t => TextureRef.Parse(t.Attribute("Path")?.Value ?? "")).ToList();
			return new MeshMaterial(name, textures);
		}

		private static uint? ParseUintAttr(XElement? el, string attr)
		{
			var v = el?.Attribute(attr)?.Value;
			return uint.TryParse(v, out var u)
				? u
				: null;
		}
	}

	public sealed record MeshMaterial(string Name, IReadOnlyList<TextureRef> Textures);

	// A material's texture slot. The client stores these as "TYPE:ID" (e.g. "1010004:4766566"); Type == null when the
	// value isn't the type:id form (then Raw carries it verbatim so nothing is silently dropped).
	public readonly record struct TextureRef(uint? Type, uint Id, string Raw)
	{
		public override string ToString()
		{
			return Type == null
				? Raw
				: $"{Type}:{Id}";
		}

		public static TextureRef Parse(string path)
		{
			var colon = path.IndexOf(':');
			if (colon > 0 && uint.TryParse(path.AsSpan(0, colon), out var type) && uint.TryParse(path.AsSpan(colon + 1), out var id))
				return new TextureRef(type, id, path);

			return new TextureRef(null, 0, path);
		}
	}

	public readonly record struct BoundingBox(float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)
	{
		public override string ToString()
		{
			return $"({MinX:0.##},{MinY:0.##},{MinZ:0.##})..({MaxX:0.##},{MaxY:0.##},{MaxZ:0.##})";
		}

		public static BoundingBox? TryParse(XElement? el)
		{
			if (el == null)
				return null;

			if (F("min_x") is { } minX && F("min_y") is { } minY && F("min_z") is { } minZ && F("max_x") is { } maxX && F("max_y") is { } maxY && F("max_z") is { } maxZ)
				return new BoundingBox(minX, minY, minZ, maxX, maxY, maxZ);

			return null;

			float? F(string name)
			{
				return float.TryParse(el.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
					? f
					: null;
			}
		}
	}
}

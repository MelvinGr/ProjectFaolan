using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Parses an Age of Conan RDB MESHINDEXFILE record (type 1010030): a small XML manifest tying a model together.
	///     It points at the geometry blob (<c>&lt;RenderMesh file="ID"/&gt;</c>, RDB type 1010001), an optional
	///     collision blob (<c>&lt;CollisionMesh2 file="ID"/&gt;</c>), and a <c>&lt;Materials&gt;</c> list whose
	///     <c>&lt;Texture Path="TYPE:ID"/&gt;</c> entries reference FCTX or PNG texture resources.
	/// </summary>
	/// <remarks>
	///     The geometry blob itself is a separate binary, decoded by <see cref="RenderMeshReader" />; this manifest is
	///     the plain-XML graph that resolves a model's textures and the geometry id to load.
	///     <para>
	///         A record is a 12-byte RDB-Id header (type, id, marker) followed by the raw XML. Most records carry no
	///         <c>&lt;?xml?&gt;</c> declaration, and file paths carry Windows backslashes inside attribute values,
	///         which is valid XML. The element is carved by root tag through <see cref="RdbXml.Carve" />: anchoring on
	///         the first '&lt;' instead latches onto a 0x3C byte inside the binary header and loses 1,693 of the 29,854
	///         manifests.
	///     </para>
	///     <para>
	///         A minority of records — 1,501 of them — are not XML at all but an older line-oriented manifest carrying
	///         the same fields (<c>Source: &lt;path&gt;</c>, <c>RenderMesh: &lt;id&gt;</c>,
	///         <c>CollisionMesh: &lt;id&gt;</c>, <c>OccluderMesh: &lt;id&gt;</c>). Those are read too, so a caller sees
	///         one record shape regardless of which form the exporter wrote.
	///     </para>
	/// </remarks>
	public sealed record MeshIndexFile(uint Id, string? SourceFile, uint? RenderMeshId, BoundingBox? Bounds, uint? CollisionMeshId, IReadOnlyList<MeshMaterial> Materials)
	{
		/// <summary>
		///     The texture resources referenced anywhere in this model's materials, de-duplicated, in first-seen order.
		/// </summary>
		public IReadOnlyList<TextureRef> UniqueTextures => Materials.SelectMany(m => m.Textures).Where(t => t.Type != null).GroupBy(t => (t.Type, t.Id)).Select(g => g.First()).ToList();

		/// <summary>
		///     Whether the geometry is a skinned character or equipment body (<c>&lt;RenderSkinnedMesh&gt;</c>). Such a
		///     body has its texture bound by the item and character system rather than by this manifest, which carries
		///     no materials for it.
		/// </summary>
		public bool Skinned { get; init; }

		public static MeshIndexFile Parse(uint id, ReadOnlySpan<byte> decompressed)
		{
			var text = Encoding.Latin1.GetString(decompressed);
			var xml = RdbXml.Carve(text, "MeshIndexFile");
			if (xml == null)
				return ParseTextForm(id, text);

			XElement root;
			try
			{
				root = XElement.Parse(xml);
			}
			catch
			{
				return new MeshIndexFile(id, null, null, null, null, []);
			}

			var source = root.Element("Source")?.Attribute("file")?.Value;

			// Static props carry <RenderMesh>; skinned character and equipment bodies carry <RenderSkinnedMesh>. Both
			// are RDB type 1010001 - the vertex declaration merely adds BLENDINDICES and BLENDWEIGHT - and the
			// geometry decodes the same way.
			var renderMeshEl = root.Element("RenderMesh") ?? root.Element("RenderSkinnedMesh");
			var renderMeshId = ParseUintAttr(renderMeshEl, "file");
			var bounds = BoundingBox.TryParse(renderMeshEl);
			var skinned = renderMeshEl?.Name.LocalName == "RenderSkinnedMesh";

			// Collision is exported as <CollisionMesh2> or, on older records, <CollisionMesh>. Only its id is kept.
			var collisionEl = root.Element("CollisionMesh2") ?? root.Element("CollisionMesh");
			var collisionId = ParseUintAttr(collisionEl, "file");

			var materials = (root.Element("Materials")?.Elements("Material") ?? []).Select(ParseMaterial).ToList();

			return new MeshIndexFile(id, source, renderMeshId, bounds, collisionId, materials)
			{
				Skinned = skinned
			};
		}

		/// <summary>
		///     Reads the older line-oriented manifest form: <c>Source: &lt;path&gt;</c>, then one
		///     <c>&lt;Kind&gt;: &lt;rdb id&gt;</c> line per referenced blob (RenderMesh, CollisionMesh, OccluderMesh).
		///     A record may list several RenderMesh lines, of which the first is the model.
		/// </summary>
		/// <returns>
		///     The parsed record, or an empty one when the text is neither form. This form carries no bounds and no
		///     materials, so the texture graph is unavailable for these records rather than wrong.
		/// </returns>
		private static MeshIndexFile ParseTextForm(uint id, string text)
		{
			string? source = null;
			uint? renderMeshId = null;
			uint? collisionId = null;

			foreach (var raw in text.Split('\n'))
			{
				var line = raw.Trim('\r', ' ', '\t', '\0');
				var colon = line.IndexOf(": ", StringComparison.Ordinal);
				if (colon <= 0)
					continue;

				var key = line[..colon];
				var value = line[(colon + 2)..].Trim();

				// The first line still carries the binary RDB-Id header ahead of "Source", so the key is matched on its
				// suffix.
				if (key.EndsWith("Source", StringComparison.Ordinal))
					source = value;
				else if (key.EndsWith("RenderMesh", StringComparison.Ordinal) && renderMeshId == null && uint.TryParse(value, out var rm))
					renderMeshId = rm;
				else if (key.EndsWith("CollisionMesh", StringComparison.Ordinal) && collisionId == null && uint.TryParse(value, out var cm))
					collisionId = cm;
			}

			return new MeshIndexFile(id, source, renderMeshId, null, collisionId, []);
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

	/// <summary>
	///     A material's texture slot. The client stores these as "TYPE:ID", for example "1010004:4766566".
	///     <c>Type</c> is null when the value is not in that form, in which case <c>Raw</c> carries it verbatim so
	///     nothing is silently dropped.
	/// </summary>
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

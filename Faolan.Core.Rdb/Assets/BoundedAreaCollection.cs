using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     The per-playfield bounded-area collection (RDB type 1000053, BOUNDEDAREAS): the client's own trigger-volume
	///     table of 8,323 named regions over 478 playfield records, covering hazards, spawn camps, resurrection points,
	///     encounter triggers, collision blockers and audio regions. The RDB record id is the playfield id (Map.Id),
	///     the same convention type 1010506 uses. The full format and a classification of the contents are in
	///     docs/rdb/bounded-areas-1000053.md.
	/// </summary>
	/// <remarks>
	///     The payload is a 12-byte header followed by plaintext XML — nothing encrypted, nothing nested-compressed —
	///     so this parses text rather than a binary grammar, and it does so with a hand-written scanner rather than an
	///     XmlReader because the documents are not well-formed by XML's rules: element bodies carry raw tabs and
	///     newlines around their values, attribute spellings vary between exports, and <c>&lt;Capped/&gt;</c>-style
	///     empties appear both self-closed and paired. An XML parser rejects such a file outright, while only four
	///     fields are needed from it.
	///     <para>
	///         The vertex ring is implicitly closed: the first point is not repeated at the end. A containment test
	///         must wrap the last edge back to vertex 0 or it leaks along that one edge.
	///     </para>
	/// </remarks>
	public sealed class BoundedAreaCollection
	{
		public const uint ResourceType = 1000053;

		public IReadOnlyList<BoundedArea> Areas { get; }

		private BoundedAreaCollection(IReadOnlyList<BoundedArea> areas)
		{
			Areas = areas;
		}

		/// <summary>
		///     Parses a decompressed 1000053 payload.
		/// </summary>
		/// <returns>
		///     False when the document carries no bounded-area root at all — a wrong resource type, or a CDN miss that
		///     handed back an empty buffer. An empty but well-formed collection is a success with zero areas, which
		///     several test playfields legitimately are.
		/// </returns>
		/// <remarks>
		///     <para>
		///         A record holds several sibling root blocks, not one: Ymir's Pass has ten
		///         (<c>&lt;boundedareas&gt;</c>, <c>&lt;boundedareas_collision&gt;</c> and further per-layer roots
		///         matching the client's BoundedAreaLayer, BoundedAreaEnvironmentLayer and SFX layers). Scanning from
		///         the first <c>&lt;boundedareas&gt;</c> to the first <c>&lt;/boundedareas&gt;</c> would therefore
		///         return 7 of that map's 121 areas and none of its lava. Every element is individually closed, so
		///         pairing each open with its own close is both simpler and correct, and it makes the sibling roots
		///         irrelevant. The layers are not distinguished: every area is returned flat.
		///     </para>
		///     <para>
		///         Two export dialects exist and both must be accepted. Newer records open the element with an
		///         attribute list, <c>&lt;boundedarea StaticCollisionEnabled="false" …&gt;</c>, while older ones write
		///         a bare <c>&lt;boundedarea&gt;</c> with no attributes at all and often no whitespace between
		///         elements. Ymir's Pass is the first kind; Tortage Volcano and the Tortage Island and Acheronian Ruins
		///         records are the second. Anchoring on <c>"&lt;boundedarea "</c> with the trailing space silently
		///         returns zero areas for 9 playfields, among them Tortage Volcano's 7 lava volumes, which is why
		///         <see cref="FindElementOpen" /> tests the delimiter instead of matching a fixed prefix.
		///     </para>
		/// </remarks>
		public static bool TryParse(ReadOnlySpan<byte> data, out BoundedAreaCollection? collection)
		{
			collection = null;

			// Latin-1, like every other string in the client's data: area names carry the odd accented character and
			// UTF-8 decoding would mangle rather than reject them.
			var xml = Encoding.Latin1.GetString(data);
			if (xml.IndexOf("<boundedarea", StringComparison.OrdinalIgnoreCase) < 0)
				return false;

			var areas = new List<BoundedArea>();
			var cursor = 0;
			while (true)
			{
				var start = FindElementOpen(xml, cursor);
				if (start < 0)
					break;

				var end = xml.IndexOf("</boundedarea>", start, StringComparison.OrdinalIgnoreCase);
				if (end < 0)
					break; // Truncated record: keep what parsed rather than discarding the whole playfield.

				var area = ParseArea(xml[start..end]);
				if (area != null)
					areas.Add(area);

				cursor = end + 14;
			}

			collection = new BoundedAreaCollection(areas);
			return true;
		}

		/// <summary>
		///     The next <c>&lt;boundedarea</c> that opens an element rather than one of the sibling root blocks. The
		///     roots all continue the word (<c>&lt;boundedareas&gt;</c>, <c>&lt;boundedareas_collision&gt;</c>,
		///     <c>&lt;boundedareas_fixed3d&gt;</c>, …), so the test is simply that the next character ends the tag
		///     name: '&gt;' for the bare dialect, whitespace for the attributed one.
		/// </summary>
		private static int FindElementOpen(string xml, int from)
		{
			while (true)
			{
				var at = xml.IndexOf("<boundedarea", from, StringComparison.OrdinalIgnoreCase);
				if (at < 0)
					return -1;

				var after = at + 12;
				if (after >= xml.Length)
					return -1;

				var c = xml[after];
				if (c == '>' || char.IsWhiteSpace(c))
					return at;

				from = after;
			}
		}

		private static BoundedArea? ParseArea(string body)
		{
			var name = Element(body, "name");
			if (name == null)
				return null;

			var vertices = new List<Vector2>();
			var cursor = 0;
			while (true)
			{
				var (value, next) = ElementAt(body, "vertex", cursor);
				if (value == null)
					break;

				cursor = next;
				var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && Float(parts[0], out var x) && Float(parts[1], out var z))
					vertices.Add(new Vector2(x, z));
			}

			// <heights> is "minY maxY". A region without one is legal - some collision blockers omit it - and the
			// caller decides what an unbounded prism means, so it is reported as NaN rather than an invented range.
			var minY = float.NaN;
			var maxY = float.NaN;
			var heights = Element(body, "heights");
			if (heights != null)
			{
				var parts = heights.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 2 && Float(parts[0], out var lo) && Float(parts[1], out var hi))
				{
					minY = MathF.Min(lo, hi);
					maxY = MathF.Max(lo, hi);
				}
			}

			// A non-zero scryid means the client runs a script on entry; 0 means it does not and any behaviour is the
			// server's. That is what separates a lava damage volume, always 0, from its ambient audio twin.
			var scry = Element(body, "scryid");
			_ = uint.TryParse(scry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var scryId);

			return new BoundedArea(name, vertices, minY, maxY, scryId);
		}

		private static bool Float(string s, out float value)
		{
			return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}

		private static string? Element(string body, string tag)
		{
			return ElementAt(body, tag, 0).Value;
		}

		/// <summary>
		///     The text between <c>&lt;tag&gt;</c> and <c>&lt;/tag&gt;</c>, trimmed of the surrounding tabs and
		///     newlines the exporter writes, plus the index to resume scanning from so that a repeated element such as
		///     <c>&lt;vertex&gt;</c> can be walked without rescanning.
		/// </summary>
		private static (string? Value, int Next) ElementAt(string body, string tag, int from)
		{
			var open = body.IndexOf('<' + tag + '>', from, StringComparison.OrdinalIgnoreCase);
			if (open < 0)
				return (null, from);

			var valueStart = open + tag.Length + 2;
			var close = body.IndexOf("</" + tag + '>', valueStart, StringComparison.OrdinalIgnoreCase);
			if (close < 0)
				return (null, from);

			return (body[valueStart..close].Trim(), close + tag.Length + 3);
		}

		/// <summary>
		///     One named region: a prism over <c>Vertices</c>, an (x, z) ring, between MinY and MaxY.
		/// </summary>
		public sealed record BoundedArea(string Name, IReadOnlyList<Vector2> Vertices, float MinY, float MaxY, uint ScryId)
		{
			/// <summary>
			///     Whether the region has enough geometry to test against. A two-vertex "polygon" encloses nothing,
			///     and a couple of records in the corpus carry one.
			/// </summary>
			public bool IsPolygon => Vertices.Count >= 3;

			/// <summary>
			///     Even-odd point-in-polygon over the (x, z) ring, wrapping the closing edge back to vertex 0. The Y
			///     range is not tested here: the caller owns that, because some regions have no
			///     <c>&lt;heights&gt;</c> at all.
			/// </summary>
			public bool Contains(float x, float z)
			{
				var inside = false;
				for (int i = 0, j = Vertices.Count - 1; i < Vertices.Count; j = i++)
				{
					var (xi, zi) = (Vertices[i].X, Vertices[i].Y);
					var (xj, zj) = (Vertices[j].X, Vertices[j].Y);
					if (zi > z != zj > z && x < (xj - xi) * (z - zi) / (zj - zi) + xi)
						inside = !inside;
				}

				return inside;
			}
		}
	}
}

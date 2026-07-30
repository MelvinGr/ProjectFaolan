using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Faolan.Core.Rdb.Assets
{
	// The per-playfield BOUNDED-AREA collection (RDB type 1000053 = BOUNDEDAREAS) - the client's own trigger-volume
	// table: 8,323 named regions over 478 playfield records, covering hazards, spawn camps, resurrection points,
	// encounter triggers, collision blockers and audio regions. The RDB record id == the playfield id (== Map.Id),
	// the same convention type 1010506 uses. Full format + the classification of what is in there:
	// docs/rdb/bounded-areas-1000053.md.
	//
	// The payload is a 12-byte header followed by PLAINTEXT XML (nothing encrypted, nothing nested-compressed), so
	// this parses text rather than a binary grammar. It is a hand-written scanner instead of an XmlReader on purpose:
	// the documents are not well-formed by XML's rules. Element bodies carry raw tabs/newlines around their values,
	// several records use attribute spellings that vary between exports, and `<Capped/>`-style empties appear both
	// self-closed and paired - an XML parser rejects the file outright, while what we actually need is four fields.
	//
	// ⚠️ The vertex ring is IMPLICITLY CLOSED: the first point is not repeated at the end. A containment test must
	// wrap the last edge back to vertex 0 or it will leak along that one edge.
	public sealed class BoundedAreaCollection
	{
		public const uint ResourceType = 1000053;

		public IReadOnlyList<BoundedArea> Areas { get; }

		private BoundedAreaCollection(IReadOnlyList<BoundedArea> areas)
		{
			Areas = areas;
		}

		// Parses a decompressed 1000053 payload. Returns false when the document carries no bounded-area root at all
		// (a wrong resource type, or a CDN miss that handed back an empty buffer) - an EMPTY but well-formed collection
		// is a success with zero areas, which several test playfields legitimately are.
		//
		// ⚠️ A record holds SEVERAL sibling root blocks, not one: Ymir's Pass has ten (`<boundedareas>`,
		// `<boundedareas_collision>`, and further per-layer roots matching the client's BoundedAreaLayer /
		// BoundedAreaEnvironmentLayer / SFX layers). An earlier version of this parser scanned from the first
		// `<boundedareas>` to the first `</boundedareas>` and so returned 7 of that map's 121 areas - including none of
		// the lava. The layers are not distinguished here because nothing needs them yet; every area is returned flat.
		//
		// The elements ARE individually closed (`</boundedarea>` matches every open), so pairing open with close is both
		// simpler and correct, and it makes the sibling roots irrelevant.
		//
		// ⚠️ There are TWO export dialects and a parser must accept both. Newer records open the element with an
		// attribute list - `<boundedarea StaticCollisionEnabled="false" …>` - while older ones write a BARE
		// `<boundedarea>` with no attributes at all (and often no whitespace between elements). Ymir's Pass is the first
		// kind, Tortage Volcano and the Tortage Island / Acheronian Ruins records are the second. Anchoring on
		// `"<boundedarea "` (with the space) therefore silently returned ZERO areas for 9 playfields, Tortage Volcano's
		// 7 lava volumes among them - a starting zone. Hence the delimiter test below rather than a fixed prefix.
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
					break; // truncated record - keep what parsed rather than discarding the whole playfield

				var area = ParseArea(xml[start..end]);
				if (area != null)
					areas.Add(area);

				cursor = end + 14;
			}

			collection = new BoundedAreaCollection(areas);
			return true;
		}

		// The next `<boundedarea` that opens an ELEMENT rather than one of the sibling ROOT blocks. The roots all
		// continue the word (`<boundedareas>`, `<boundedareas_collision>`, `<boundedareas_fixed3d>`, …), so the test is
		// simply that the next character ends the tag name: '>' for the bare dialect, whitespace for the attributed one.
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

			// `<heights>` is "minY maxY". A region without one is legal (some collision blockers omit it); the caller
			// decides what an unbounded prism means, so report it as NaN rather than inventing a range.
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

			// A non-zero scryid means the CLIENT runs a script on entry; 0 means it does not and any behaviour is the
			// server's. That is what separates a lava DAMAGE volume (always 0) from its ambient audio twin.
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

		// The text between `<tag>` and `</tag>`, trimmed of the surrounding tabs/newlines the exporter writes, plus the
		// index to resume scanning from (so a repeated element like `<vertex>` can be walked without rescanning).
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

		// One named region: a prism over `Vertices` (an (x, z) ring) between MinY and MaxY.
		public sealed record BoundedArea(string Name, IReadOnlyList<Vector2> Vertices, float MinY, float MaxY, uint ScryId)
		{
			// Whether the region has enough geometry to test against - a 2-vertex "polygon" encloses nothing, and a
			// couple of records in the corpus carry one.
			public bool IsPolygon => Vertices.Count >= 3;

			// Even-odd point-in-polygon over the (x, z) ring, wrapping the closing edge back to vertex 0. The Y range is
			// NOT tested here: the caller owns that, because some regions have no `<heights>` at all.
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

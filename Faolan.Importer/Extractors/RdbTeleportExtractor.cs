using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Extractors
{
	/// <summary>One row of the client's own cross-playfield teleport table (RDB 1000087).</summary>
	/// <param name="SourceMapId">The playfield the door stands in (`SourcePlayfieldID`).</param>
	/// <param name="X">Where in it, in playfield coordinates (`SourcePosX/Y/Z`).</param>
	/// <param name="Y">See <paramref name="X" />.</param>
	/// <param name="Z">See <paramref name="X" />.</param>
	/// <param name="DestMapId">The playfield it leads to (`DestinationPlayfieldID`).</param>
	public sealed record RdbTeleport(uint SourceMapId, float X, float Y, float Z, uint DestMapId);

	// RDB **1000087** is the client's COMPLETE cross-playfield teleport table, and it is one plain-text XML record
	// (id 0, ~140 KB) rather than a per-playfield resource (Gitea #883):
	//
	//   <teleportdata><teleports>
	//     <teleportplayfield ExportUser="..." SourcePlayfieldID="1">
	//       <teleport SourcePosX="25.0" SourcePosY="10.0898" SourcePosZ="35.0" DestinationPlayfieldID="2"/>
	//     </teleportplayfield>
	//   ...
	//
	// 675 teleports over 528 source playfields, reaching 212 destinations. Against that, the capture harvest
	// (`WorldTeleportExtractor`) holds 160 links over 24 source maps - it can only ever see a door somebody actually
	// walked through on camera.
	//
	// ⚠️ **It carries NO destination coordinates and NO door name** - the `<teleport>` element has exactly four
	// attributes, censused across all 675, not read off the first one. Both absences matter downstream:
	//   * no arrival -> the runtime falls back to the destination map's default spawn, which is a path
	//     `WorldTeleportsModule` already has and already documents. A captured link, which DOES carry the arrival,
	//     must therefore always win over an RDB one.
	//   * no name -> the match back to a `WorldObject` row is POSITION-ONLY, where the captured tier can also
	//     require the name to agree. That is a weaker key, so the caller has to handle two doors near one point
	//     rather than pick the closer.
	public static class RdbTeleportExtractor
	{
		/// <summary>The whole table is this one record.</summary>
		public const uint ResourceType = 1000087;

		public const uint RecordId = 0;

		private static readonly Regex Playfield = new(@"<teleportplayfield\b[^>]*\bSourcePlayfieldID=""(\d+)""[^>]*>(.*?)</teleportplayfield>",
			RegexOptions.Singleline | RegexOptions.Compiled);

		private static readonly Regex Teleport = new(
			@"<teleport\b[^>]*\bSourcePosX=""([-\d.eE+]+)""[^>]*\bSourcePosY=""([-\d.eE+]+)""[^>]*\bSourcePosZ=""([-\d.eE+]+)""[^>]*\bDestinationPlayfieldID=""(\d+)""",
			RegexOptions.Compiled);

		/// <summary>Every teleport the client's table states, or an empty list when the record is absent.</summary>
		public static IReadOnlyList<RdbTeleport> Extract(RdbReader rdb)
		{
			ArgumentNullException.ThrowIfNull(rdb);
			if (!rdb.TryGet(ResourceType, RecordId, out var entry))
				return [];

			// Latin1, not UTF8: the body opens with the 12-byte RDB data header before the `<?xml`, and a UTF8 decode
			// substitutes over those bytes. Same rule as every other text RDB record.
			var text = Encoding.Latin1.GetString(rdb.ReadDecompressed(entry));

			return Parse(text);
		}

		/// <summary>The parse proper, split out so a test can drive it from a literal rather than from the client.</summary>
		public static IReadOnlyList<RdbTeleport> Parse(string xml)
		{
			ArgumentNullException.ThrowIfNull(xml);

			return (from playfield in Playfield.Matches(xml).Cast<Match>()
				let sourceMapId = uint.Parse(playfield.Groups[1].Value, CultureInfo.InvariantCulture)
				from teleport in Teleport.Matches(playfield.Groups[2].Value).Cast<Match>()
				select new RdbTeleport(sourceMapId, F(teleport.Groups[1].Value), F(teleport.Groups[2].Value), F(teleport.Groups[3].Value),
					uint.Parse(teleport.Groups[4].Value, CultureInfo.InvariantCulture))).ToList();
		}

		private static float F(string value)
		{
			return float.Parse(value, CultureInfo.InvariantCulture);
		}
	}
}

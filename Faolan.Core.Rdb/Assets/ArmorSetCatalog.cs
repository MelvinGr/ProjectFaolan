using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.Core.Rdb.Assets
{
	/// <summary>
	///     Builds a catalogue of wearable armour sets from the client RDB MESHINDEXFILE manifests. Each skinned armour
	///     mesh lives under <c>...\items\armor\_sets\NNNN_&lt;setname&gt;_&lt;gender&gt;\...</c>, so grouping by
	///     (set number, gender) gives the viewer a full set's worth of meshindex pieces to dress a character in via the
	///     equipment-overlay pipeline.
	/// </summary>
	/// <remarks>
	///     This is the wardrobe half of wearing gear: it previews any authentic armour set on a character. Resolving
	///     which meshes a specific equipped item wears is <see cref="ItemLookResolver" />'s job.
	/// </remarks>
	public sealed partial class ArmorSetCatalog
	{
		[GeneratedRegex(@"_sets[\\/](\d+)[ _]?(.*?)[\\/]", RegexOptions.IgnoreCase)]
		private static partial Regex SetPathRegex();

		/// <summary>
		///     Scans every MESHINDEXFILE once, keeping the skinned armour-set pieces grouped by (set number, gender).
		///     Sets are ordered by number then gender; pieces keep first-seen order.
		/// </summary>
		/// <remarks>
		///     This is a full index scan and therefore expensive, so callers should cache the result.
		/// </remarks>
		public static IReadOnlyList<ArmorSet> Build(RdbReader rdb)
		{
			var groups = new Dictionary<(int Number, string Gender), (string Name, List<uint> Ids)>();

			foreach (var e in rdb.EntriesOfType(RdbAssetExtractor.MeshIndexFileType))
			{
				if (e.Size == 0 || e.File == 0xff)
					continue;

				MeshIndexFile mesh;
				try
				{
					mesh = MeshIndexFile.Parse(e.Id, rdb.ReadDecompressed(e));
				}
				catch
				{
					continue;
				}

				var src = mesh.SourceFile;
				if (!mesh.Skinned || src == null || mesh.RenderMeshId == null)
					continue;

				var m = SetPathRegex().Match(src);
				if (!m.Success || !int.TryParse(m.Groups[1].Value, out var number))
					continue;

				var lower = src.ToLowerInvariant();
				var gender = lower.Contains("female")
					? "Female"
					: lower.Contains("male")
						? "Male"
						: "Unisex";
				var name = CleanName(m.Groups[2].Value);

				var key = (number, gender);
				if (!groups.TryGetValue(key, out var g))
					groups[key] = g = (name, []);
				if (string.IsNullOrEmpty(g.Name) && !string.IsNullOrEmpty(name))
					groups[key] = (name, g.Ids);
				g.Ids.Add(e.Id);
			}

			return groups.Select(kv => new ArmorSet(kv.Key.Number, kv.Key.Gender, kv.Value.Name, kv.Value.Ids)).OrderBy(s => s.Number).ThenBy(s => s.Gender).ToList();
		}

		/// <summary>
		///     Tidies a raw folder fragment into a readable set name: underscores become spaces, the trailing
		///     male/female word and the leading "+NNNN"/"-NNNN"/"set" tokens the export folders prefix are dropped, and
		///     whitespace is collapsed.
		/// </summary>
		private static string CleanName(string raw)
		{
			var s = raw.Replace('_', ' ').Replace('+', ' ');
			s = Regex.Replace(s, @"\b(fe)?male\b", "", RegexOptions.IgnoreCase);
			s = Regex.Replace(s, @"\bset\b", "", RegexOptions.IgnoreCase);
			s = Regex.Replace(s, @"[-+]?\d{3,4}", " "); // stray set-number tokens
			s = Regex.Replace(s, @"\s+", " ").Trim();
			return s;
		}

		public sealed record ArmorSet(int Number, string Gender, string Name, IReadOnlyList<uint> PieceMeshIndexIds);
	}
}

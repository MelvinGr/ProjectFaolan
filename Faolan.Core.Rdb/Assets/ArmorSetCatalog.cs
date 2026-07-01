using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.Core.Rdb.Assets
{
	// Builds a catalogue of wearable armour SETS from the client RDB MESHINDEXFILE manifests (#275 gear). Each skinned
	// armour mesh lives under `...\items\armor\_sets\NNNN_<setname>_<gender>\...`; we group those by (set number, gender)
	// so the viewer can dress a character in a full set's worth of meshindex pieces via the equipment-overlay pipeline.
	//
	// This is the WARDROBE half of "wearing gear": it lets the viewer preview any authentic armour set on a character.
	// It is NOT the per-item look mapping (which decrypted item-template field selects WHICH set for a given equipped
	// item) - that appearance id is buried in the 1000020 template's trailing binary sub-record and is still open RE.
	public sealed partial class ArmorSetCatalog
	{
		[GeneratedRegex(@"_sets[\\/](\d+)[ _]?(.*?)[\\/]", RegexOptions.IgnoreCase)]
		private static partial Regex SetPathRegex();

		// Scan every MESHINDEXFILE once, keep the skinned armour-set pieces, group by (set number, gender). Ordered by set
		// number then gender. Pieces keep first-seen order. Expensive (full index scan) so callers should cache the result.
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
				var gender = lower.Contains("female") ? "Female" : lower.Contains("male") ? "Male" : "Unisex";
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

		// Tidy a raw folder fragment into a readable set name: underscores->spaces, drop the trailing male/female word and
		// any leading "+NNNN" / "-NNNN" / "set" tokens the export folders prefix, collapse whitespace.
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

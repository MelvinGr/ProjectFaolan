using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Imports the character-creator appearance choices (RDB type 1010700 - plaintext XML with the per-sex/race legal
	// lists of hair / colours / tattoos / heads + a height range + per-sex morph min/max) into AppearanceChoice rows,
	// resolving each option's friendly Name from the referenced RDB object's own name= (texscript for tattoos,
	// monsterdata for heads). Colours store the swatch hex from the XML. The Faolan.Web appearance editor reads these
	// rows for its race/sex-gated pickers. (Polished hair/head localized names live in the LDB - type 1030002 - whose
	// per-id index is not yet decoded; hair falls back to an ordinal label until then.)
	public static class RdbAppearanceImporter
	{
		private const uint CreatorType = 1010700;
		private const uint MonsterDataType = 1010201;
		private const uint TexScriptType = 1010035;

		public static async Task<int> ImportAsync(DatabaseContext db, string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var entry = rdb.EntriesOfType(CreatorType).OrderByDescending(e => e.Size).FirstOrDefault();
			if (entry.Size == 0)
			{
				Console.WriteLine($"RDB appearance: CHARACTERCREATOR type {CreatorType} not found; skipping.");
				return 0;
			}

			var bytes = rdb.ReadDecompressed(entry);
			var start = Array.IndexOf(bytes, (byte)'<');
			var root = XDocument.Parse(Encoding.UTF8.GetString(bytes,
					start < 0
						? 0
						: start,
					bytes.Length - (start < 0
						? 0
						: start)))
				.Root!;

			// name= of a referenced RDB resource (cached; the same texscript is shared across races).
			var nameCache = new Dictionary<(uint, uint), string>();

			var rows = new List<AppearanceChoice>();
			foreach (var sexEl in root.Elements())
			{
				var sex = sexEl.Name.LocalName.Equals("female", StringComparison.OrdinalIgnoreCase)
					? Sex.Female
					: Sex.Male;

				// Per-sex morph min/max, stored once per race (so a single (sex,race) query returns them).
				var morphs = sexEl
					.Elements("BodyNodes")
					.Elements("MorphNode")
					.Select((mn, i) => (Name: (string)mn.Attribute("MorphName"), Min: Scale(mn.Attribute("min_value")), Max: Scale(mn.Attribute("max_value")), Seq: i))
					.Where(m => m.Name != null)
					.ToList();

				foreach (var raceEl in sexEl.Elements())
				{
					if (!TryRace(raceEl.Name.LocalName, out var race))
						continue;

					foreach (var m in morphs)
						rows.Add(Row(sex, race, "morph", m.Seq, m.Name, null, null, m.Min, m.Max));

					var h = raceEl.Element("height");
					if (h != null)
						rows.Add(Row(sex, race, "height", 0, "", null, null, (int?)h.Attribute("min") ?? 0, (int?)h.Attribute("max") ?? 0));

					AddList(rows, sex, race, "hair", raceEl.Elements("hair"), "meshid", "*hairmesh_", null, null);
					AddList(rows, sex, race, "haircolor", raceEl.Elements("haircolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "eyecolor", raceEl.Elements("eyecolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "skincolor", raceEl.Elements("skincolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "bodymarkings", raceEl.Elements("bodymarkings"), "texscript", "*texscript_", null, id => CleanTexName(ResourceName(TexScriptType, id)));
					AddList(rows, sex, race, "facemarkings", raceEl.Elements("facemarkings"), "texscript", "*texscript_", null, id => CleanTexName(ResourceName(TexScriptType, id)));

					var hseq = 0;
					foreach (var he in raceEl.Elements("head"))
					{
						var mid = (uint?)(int?)he.Attribute("monsterdataid") ?? 0;
						if (mid == 0)
							continue;

						rows.Add(Row(sex, race, "head", hseq++, mid.ToString(), null, CleanHeadName(ResourceName(MonsterDataType, mid)), 0, 0));
					}
				}
			}

			await db.AppearanceChoices.ExecuteDeleteAsync();
			db.AppearanceChoices.AddRange(rows);
			var n = await db.SaveChangesAsync();
			Console.WriteLine($"RDB appearance: imported {rows.Count} choices across {rows.Select(r => (r.Sex, r.Race)).Distinct().Count()} sex/race combos.");
			return n;

			string ResourceName(uint type, uint id)
			{
				if (nameCache.TryGetValue((type, id), out var cached))
					return cached;

				string name = null;
				if (rdb.TryGet(type, id, out var e))
				{
					try
					{
						var data = rdb.ReadDecompressed(e);
						var text = Encoding.ASCII.GetString(data, 0, Math.Min(400, data.Length));
						var m = Regex.Match(text, "name=\"([^\"]+)\"");
						if (m.Success)
							name = m.Groups[1].Value;
					}
					catch
					{
						// leave null
					}
				}

				nameCache[(type, id)] = name;
				return name;
			}
		}

		// Adds one category's option elements (id "0" = the creator's none placeholder, skipped). colourAttr set ->
		// store the hex swatch; nameFn set -> resolve a friendly name from the resource.
		private static void AddList(List<AppearanceChoice> rows, Sex sex, Race race, string category, IEnumerable<XElement> elems, string idAttr, string prefix, string colourAttr, Func<uint, string> nameFn)
		{
			var seq = 0;
			foreach (var e in elems)
			{
				var idText = (string)e.Attribute(idAttr);
				if (string.IsNullOrEmpty(idText) || idText == "0" || !uint.TryParse(idText, out var id))
					continue;

				var colour = colourAttr != null
					? (string)e.Attribute(colourAttr)
					: null;
				var name = nameFn?.Invoke(id);
				rows.Add(Row(sex, race, category, seq++, prefix + id, colour, name, 0, 0));
			}
		}

		// "female_aquilonian_generic_thick_1_head" -> "Generic Thick 1".
		private static string CleanHeadName(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return null;

			var s = raw;
			foreach (var strip in new[]
			{
				"female_",
				"male_",
				"aquilonian_",
				"cimmerian_",
				"stygian_",
				"khitan_",
				"generic_",
				"_head"
			})
				s = s.Replace(strip, "", StringComparison.OrdinalIgnoreCase);
			s = s.Replace('_', ' ').Trim();
			if (s.Length == 0)
				return null;

			return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
		}

		// "Face (Tattoo) Cimmerian - The Mountain Cat (F)" -> "The Mountain Cat". Keeps the whole string if it has no
		// " - " segment.
		private static string CleanTexName(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return null;

			var dash = raw.LastIndexOf(" - ", StringComparison.Ordinal);
			var s = dash >= 0
				? raw[(dash + 3)..]
				: raw;
			s = Regex.Replace(s, @"\s*\((?:F|M)\)\s*$", "").Trim();
			return s.Length > 0
				? s
				: null;
		}

		private static AppearanceChoice Row(Sex sex, Race race, string category, int seq, string value, string colour, string name, int min, int max)
		{
			return new AppearanceChoice
			{
				Sex = (byte)sex,
				Race = (byte)race,
				Category = category,
				Sequence = seq,
				Value = value,
				Color = colour,
				Name = name,
				Min = min,
				Max = max
			};
		}

		private static int Scale(XAttribute attr)
		{
			return attr != null && double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
				? (int)Math.Round(v * 1000)
				: 0;
		}

		private static bool TryRace(string name, out Race race)
		{
			switch (name.ToLowerInvariant())
			{
				case "aquilonian":
					race = Race.Aquilonian;
					return true;
				case "cimmerian":
					race = Race.Cimmerian;
					return true;
				case "stygian":
					race = Race.Stygian;
					return true;
				default:
					race = default;
					return false;
			}
		}
	}
}

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

			// Carve by ROOT TAG, not from the first '<': the 12-byte RDB-Id header ahead of the XML is binary and can
			// contain a 0x3C byte, which would anchor the parse inside it (see RdbXml).
			var xml = RdbXml.Carve(Encoding.UTF8.GetString(bytes), "root");
			if (xml == null)
			{
				Console.WriteLine($"RDB appearance: CHARACTERCREATOR {entry.Id} has no <root> element; skipping.");
				return 0;
			}

			var root = XElement.Parse(xml);

			// name= of a referenced RDB resource (cached; the same texscript is shared across races).
			var nameCache = new Dictionary<(uint, uint), string?>();

			var rows = new List<AppearanceChoice>();
			var sexRows = new List<AppearanceSexOption>();
			var seenElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var sexEl in root.Elements())
			{
				var sex = sexEl.Name.LocalName.Equals("female", StringComparison.OrdinalIgnoreCase)
					? Sex.Female
					: Sex.Male;

				// Per-sex morph min/max, stored once per race (so a single (sex,race) query returns them).
				var morphs = sexEl
					.Elements("BodyNodes")
					.Elements("MorphNode")
					.Select((mn, i) => (Name: (string?)mn.Attribute("MorphName"), Min: Scale(mn.Attribute("min_value")), Max: Scale(mn.Attribute("max_value")), Seq: i))
					.Where(m => m.Name != null)
					.ToList();

				// FACE morphs, the sibling of BodyNodes above and unimported until the coverage check below pointed at
				// it: 21 more MorphNode ranges per sex (EyebrowScale, BrowLow/BrowHigh, ...). Same shape, own category.
				var faceMorphs = sexEl
					.Elements("FaceNodes")
					.Elements("MorphNode")
					.Select((mn, i) => (Name: (string?)mn.Attribute("MorphName"), Min: Scale(mn.Attribute("min_value")), Max: Scale(mn.Attribute("max_value")), Seq: i))
					.Where(m => m.Name != null)
					.ToList();

				// The five create-editor voice samples per sex: [enum][wav path].
				var voices = sexEl
					.Elements("voice")
					.Select((v, i) => (Value: (string?)v.Attribute("enum"), File: (string?)v.Attribute("file"), Seq: i))
					.Where(v => v.Value != null)
					.ToList();

				// PER-SEX, written ONCE - these hang off the sex element, not a race one. They used to be duplicated
				// into every race so a single (sex, race) query returned them, which stored 66 definitions as 264 rows
				// and implied morphs vary by race. They do not.
				foreach (var m in morphs)
					sexRows.Add(SexRow(sex, "morph", m.Seq, m.Name!, null, m.Min, m.Max)); // Name != null by the Where above

				foreach (var m in faceMorphs)
					sexRows.Add(SexRow(sex, "facemorph", m.Seq, m.Name!, null, m.Min, m.Max));

				foreach (var v in voices)
					sexRows.Add(SexRow(sex, "voice", v.Seq, v.Value!, v.File, 0, 0));

				foreach (var raceEl in sexEl.Elements())
				{
					// The sex element's non-race children (BodyNodes / FaceNodes / voice) are read ABOVE, per sex
					// rather than per race - skip them here without complaining, or the guard cries wolf on data it
					// already has.
					if (SexLevelElements.Contains(raceEl.Name.LocalName))
						continue;

					if (!TryRace(raceEl.Name.LocalName, out var race))
					{
						// Say so. A race element we cannot name is appearance data being dropped, and the silent
						// `continue` here is what hid Khitan (#469).
						Console.WriteLine($"RDB appearance: unknown race element <{raceEl.Name.LocalName}> - its choices are SKIPPED.");
						continue;
					}

					var h = raceEl.Element("height");
					if (h != null)
						rows.Add(Row(sex, race, "height", 0, "", null, null, (int?)h.Attribute("min") ?? 0, (int?)h.Attribute("max") ?? 0));

					AddList(rows, sex, race, "hair", raceEl.Elements("hair"), "meshid", "*hairmesh_", null, null);
					AddList(rows, sex, race, "haircolor", raceEl.Elements("haircolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "eyecolor", raceEl.Elements("eyecolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "skincolor", raceEl.Elements("skincolor"), "texscript", "*texscript_", "color", null);
					AddList(rows, sex, race, "bodymarkings", raceEl.Elements("bodymarkings"), "texscript", "*texscript_", null, id => CleanTexName(ResourceName(TexScriptType, id)));
					AddList(rows, sex, race, "facemarkings", raceEl.Elements("facemarkings"), "texscript", "*texscript_", null, id => CleanTexName(ResourceName(TexScriptType, id)));

					// beard and piercings are MESH lists exactly like hair (`meshid`, "0" = the creator's none
					// placeholder), and were simply never read - 294 beard options and 14 piercing ones dropped on
					// every import. `body` is one monsterdataid per race/sex: the value Faolan.Web's CharacterModelService
					// currently carries as a hardcoded BodyMonster table, so that table can now be sourced instead.
					AddList(rows, sex, race, "beard", raceEl.Elements("beard"), "meshid", "*hairmesh_", null, null);
					AddList(rows, sex, race, "piercings", raceEl.Elements("piercings"), "meshid", "*hairmesh_", null, null);
					AddList(rows, sex, race, "body", raceEl.Elements("body"), "monsterdataid", "", null, null);

					// Anything in the XML this method does not read is appearance data silently dropped - which is
					// exactly how beard (294 options), piercings and body sat unimported for as long as they did.
					// Record what the file actually contains so the check after the loop can compare.
					foreach (var child in raceEl.Elements())
						seenElements.Add(child.Name.LocalName);

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

			// ⚠️ COVERAGE CHECK. The suite cannot do this - it runs from a plain clone with no RDB - so the guard
			// lives here, where the file is open. A client patch that adds a category would otherwise be imported as
			// "nothing", and the row count would still look plausible: that is precisely how beard, piercings and
			// body were missed. Loud rather than fatal, because a new element is content to add, not corruption.
			var unhandled = seenElements.Except(HandledElements, StringComparer.OrdinalIgnoreCase).OrderBy(e => e, StringComparer.Ordinal).ToList();
			if (unhandled.Count > 0)
				Console.WriteLine($"RDB appearance: ⚠️  {unhandled.Count} XML element(s) NOT imported - {string.Join(", ", unhandled.Select(e => $"<{e}>"))}. Add them to AddList and to HandledElements.");

			await db.AppearanceChoices.ExecuteDeleteAsync();
			await db.AppearanceSexOptions.ExecuteDeleteAsync();
			db.AppearanceChoices.AddRange(rows);
			db.AppearanceSexOptions.AddRange(sexRows);
			var n = await db.SaveChangesAsync();
			Console.WriteLine($"RDB appearance: imported {rows.Count} per-race choices across {rows.Select(r => (r.Sex, r.Race)).Distinct().Count()} sex/race combos, plus {sexRows.Count} per-sex options.");
			return n;

			string? ResourceName(uint type, uint id)
			{
				if (nameCache.TryGetValue((type, id), out var cached))
					return cached;

				string? name = null;
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
		private static void AddList(List<AppearanceChoice> rows, Sex sex, Race race, string category, IEnumerable<XElement> elems, string idAttr, string prefix, string? colourAttr,
			Func<uint, string?>? nameFn)
		{
			var seq = 0;
			foreach (var e in elems)
			{
				var idText = (string?)e.Attribute(idAttr);
				if (string.IsNullOrEmpty(idText) || idText == "0" || !uint.TryParse(idText, out var id))
					continue;

				var colour = colourAttr != null
					? (string?)e.Attribute(colourAttr)
					: null;
				var name = nameFn?.Invoke(id);
				rows.Add(Row(sex, race, category, seq++, prefix + id, colour, name, 0, 0));
			}
		}

		// "female_aquilonian_generic_thick_1_head" -> "Generic Thick 1".
		private static string? CleanHeadName(string? raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return null;

			var s = raw;
			foreach (var strip in new[] { "female_", "male_", "aquilonian_", "cimmerian_", "stygian_", "khitan_", "generic_", "_head" })
				s = s.Replace(strip, "", StringComparison.OrdinalIgnoreCase);
			s = s.Replace('_', ' ').Trim();
			if (s.Length == 0)
				return null;

			return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
		}

		// "Face (Tattoo) Cimmerian - The Mountain Cat (F)" -> "The Mountain Cat". Keeps the whole string if it has no
		// " - " segment.
		private static string? CleanTexName(string? raw)
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

		private static AppearanceSexOption SexRow(Sex sex, string category, int seq, string value, string? name, int min, int max)
		{
			return new AppearanceSexOption
			{
				Sex = sex,
				Category = category,
				Sequence = seq,
				Value = value,
				Name = name,
				Min = min,
				Max = max
			};
		}

		private static AppearanceChoice Row(Sex sex, Race race, string category, int seq, string value, string? colour, string? name, int min, int max)
		{
			return new AppearanceChoice
			{
				Sex = sex,
				Race = race,
				Category = category,
				Sequence = seq,
				Value = value,
				Color = colour,
				Name = name,
				Min = min,
				Max = max
			};
		}

		private static int Scale(XAttribute? attr)
		{
			return attr != null && double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
				? (int)Math.Round(v * 1000)
				: 0;
		}

		// Every per-race child element this importer reads. Kept beside the reads deliberately: adding an AddList call
		// without adding its name here makes the coverage check complain about an element that IS handled, which is a
		// far better failure than the reverse.
		// Children of the SEX element that are not races - read per sex, above the race loop.
		private static readonly HashSet<string> SexLevelElements = new(StringComparer.OrdinalIgnoreCase) { "BodyNodes", "FaceNodes", "voice" };

		private static readonly string[] HandledElements =
		[
			"height", "hair", "haircolor", "eyecolor", "skincolor", "bodymarkings", "facemarkings", "head", "beard", "piercings", "body"
		];

		private static bool TryRace(string name, out Race race)
		{
			switch (name.ToLowerInvariant())
			{
				case "aquilonian":
				{
					race = Race.Aquilonian;
					return true;
				}

				case "cimmerian":
				{
					race = Race.Cimmerian;
					return true;
				}

				case "stygian":
				{
					race = Race.Stygian;
					return true;
				}

				// Rise of the Godslayer. The CHARACTERCREATOR XML has carried a <khitan> section all along and this
				// switch did not know the name, so every Khitan choice fell through to `default` and was dropped by
				// the caller's `continue` - silently, which is how it went unnoticed (#469).
				case "khitan":
				{
					race = Race.Khitan;
					return true;
				}

				default:
				{
					race = default;
					return false;
				}
			}
		}
	}
}

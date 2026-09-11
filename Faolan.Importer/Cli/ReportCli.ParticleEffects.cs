using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report particle-effects [--report-arg <substring>|xml:<id>]`: every RDB 1010207 particle effect, named.
	//
	// The server never renders a particle effect - it sends an id on `SimpleDynelVisualChangeIIR
	// e_StartParticleEffect` and the client does the rest - so the only question it ever asks of this type is
	// "which id is the one I want". This report answers it two ways, and the second one is the good one.
	//
	// The catalog page recorded this payload as "an undecoded zlib blob (a binary effect graph, no strings)".
	// Both halves were wrong, and each for its own reason.
	//
	// 1. "no strings" was the `pack_grep_lies` trap: the record is zlib, so a grep over the pack file finds nothing
	//    and the text only appears once it is inflated. Inflated, 559 records carry the build machine's own paths
	//    for the textures they draw with (`W:\achardb\conan\particles\smoke\gradient_ab.png`).
	//
	// 2. "undecoded" was the `some_rdb_text_xml` trap: 189 of the 8,163 records are not the binary form at all -
	//    they are the ARTIST'S AUTHORED XML, shipped verbatim. `<particlesystem>` with a 192-tag schema: systems,
	//    emitters, particles, textures, lights, animated curves with control points, and an `<exportpath>` carrying
	//    the original source file name. So the effect graph's schema is not a reverse-engineering problem - the
	//    client ships the documentation. Same shape as RDB 1010203 being plain-text XML while its 1010501 twin is
	//    the merged binary.
	//
	// and the name table INDEXES this type, so 7,779 of the 8,163 (95.3%) have a name - not just the 189.
	// This report shipped naming only the authored ones until chasing RDB 1010031 (attractor-FX placements, which
	// reference an effect by name) showed the name table resolves those names to real 1010207 ids: of the 69 names
	// those placements use, 64 resolve and all 64 land on an existing record, with no ambiguity. So the name table
	// is the primary name source here and the authored path is extra context, not the other way round.
	//
	// A name beats a texture list at the "which id" question by a mile: `spell_demonologist_summonpet_gather35`,
	// `combo_generic_pbaoe_knockback_bright2`, `environ_water_jump_splash` say what the effect is. And the id a name
	// resolves to is the wire value - `SimpleDynelVisualChangeIIR e_StartParticleEffect` carries it.
	//
	// The two populations are DISJOINT - a record is either the XML or the compiled binary, never both - so the
	// XML ones cannot be used as a Rosetta stone to decode their own binary twins (there are none). Zero of the
	// 7,974 binary records carry a source path. Decoding the binary form still needs the client's reader; what the
	// XML gives is the field list to expect when someone does that.
	public static partial class ReportCli
	{
		private const uint ParticleEffectType = 1010207;

		// The build machine's own asset paths, as they survive in the payload.
		private static readonly Regex AssetPath = new(@"[A-Za-z]:\\[ -~]{4,120}?\.(?:png|tga|dds|jpg)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		// The authored source's own name for the effect. Only the 189 XML-form records carry it.
		private static readonly Regex ExportPath = new(@"<exportpath>(.*?)</exportpath>", RegexOptions.Singleline | RegexOptions.Compiled);

		// `loopcountmax` is the one field here the server can act on: a one-shot ends by itself, a looping one runs
		// until something stops it. Retail's authoring tool writes "forever" as a saturated int rather than a flag.
		private static readonly Regex LoopCountMax = new(@"<loopcountmax>(.*?)</loopcountmax>", RegexOptions.Singleline | RegexOptions.Compiled);
		private static readonly Regex DurationMax = new(@"<durationmax>(.*?)</durationmax>", RegexOptions.Singleline | RegexOptions.Compiled);

		// Anything at or above this reads as "loop forever" - the authored values are 50,000,000 / 2,000,000,000,
		// which are saturated sentinels, not counts anyone intends to reach.
		private const long LoopsForever = 1_000_000;

		private static void ParticleEffects(string? rdbDir, string? filter)
		{
			var rdb = string.IsNullOrEmpty(rdbDir) ? RdbDir() : rdbDir;
			if (string.IsNullOrEmpty(rdb) || !Directory.Exists(rdb))
			{
				Console.WriteLine("particle-effects needs the client RDB: pass --Content:RdbDir <dir>.");
				return;
			}

			using var reader = OpenRdb(rdb);
			var entries = reader.EntriesOfType(ParticleEffectType).ToList();

			// The name table is the primary name source and covers 95.3% of the type - far more than the 189
			// authored-XML records do. This report shipped naming only those 189 until the attractor chase
			// showed the name table indexes 1010207 too.
			var names = RdbNameTable.Load(reader);

			// `xml:<id>` prints one authored source whole - the schema is worth reading rather than summarising.
			if (filter != null && filter.StartsWith("xml:", StringComparison.Ordinal))
			{
				PrintAuthoredSource(reader, entries, filter[4..]);
				return;
			}

			int unreadable = 0, authored = 0, withAssets = 0;
			var rows = new List<Effect>();

			foreach (var e in entries)
			{
				byte[] body;
				try
				{
					body = reader.ReadDecompressed(e);
				}
				catch (Exception)
				{
					unreadable++;
					continue;
				}

				// Latin1 so every byte maps to a char - the payload is binary with text embedded, not text.
				var text = System.Text.Encoding.Latin1.GetString(body);
				var assets = AssetPath.Matches(text).Select(m => m.Value).Distinct().ToList();
				var isXml = text.Contains("<particlesystem>", StringComparison.Ordinal);
				var authoredPath = isXml && ExportPath.Match(text) is { Success: true } m ? EffectName(m.Groups[1].Value) : null;

				// The name table's name wins for the LABEL (95.3% coverage); the authored path is kept beside it
				// because it carries the artist's folder, which the flat name does not.
				var name = names.TryGetValue(e.Id, out var indexed) ? TrimEffectSuffix(indexed) : null;

				if (isXml)
					authored++;
				if (assets.Count > 0)
					withAssets++;
				if (name == null && authoredPath == null && assets.Count == 0)
					continue;

				rows.Add(new Effect(e.Id, name, authoredPath, assets, isXml ? Looping(text) : null));
			}

			Console.WriteLine($"particle-effects: {entries.Count:N0} records of RDB {ParticleEffectType}, {unreadable:N0} would not inflate.");
			Console.WriteLine($"  {rows.Count(r => r.Name != null):N0} are NAMED by the RDB name table (1000010/1).");
			Console.WriteLine($"  {authored:N0} are the artist's authored XML - those also carry a folder path and a duration; {entries.Count - authored:N0} are the compiled binary.");
			Console.WriteLine($"  {withAssets:N0} carry at least one texture asset path.");

			var matched = filter == null
				? rows
				: rows.Where(r => (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
					|| (r.AuthoredPath?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
					|| r.Assets.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();

			if (filter != null)
				Console.WriteLine($"  filtered to name or texture containing \"{filter}\": {matched.Count:N0} record(s).");

			Console.WriteLine();
			Console.WriteLine("  the id IS what goes on the wire (SimpleDynelVisualChangeIIR e_StartParticleEffect).");
			Console.WriteLine("  `loops` = loopcountmax says the effect runs until something STOPS it; a time ends by itself.");
			Console.WriteLine("  a row with no name is one of the 4.7% the name table does not index.");
			Console.WriteLine();

			var limit = filter == null ? 40 : 400;
			foreach (var r in matched.OrderBy(r => r.Name == null).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id).Take(limit))
			{
				var label = r.Name ?? string.Join("  |  ", r.Assets.Select(ShortAsset));
				var extra = r.AuthoredPath != null ? $"   [{r.AuthoredPath}]" : "";
				Console.WriteLine($"  {ParticleEffectType}:{r.Id,-10} {r.Loop,-5} {label}{extra}");
			}

			if (matched.Count > limit)
				Console.WriteLine($"  ... and {matched.Count - limit:N0} more - pass --report-arg <substring> to search by name or texture.");

			Console.WriteLine();
			Console.WriteLine($"  --report-arg xml:<id> prints one authored source whole (e.g. xml:{rows.FirstOrDefault(r => r.Name != null)?.Id.ToString() ?? "3943949"}).");
		}

		private static void PrintAuthoredSource(RdbReader reader, List<RdbReader.Entry> entries, string idText)
		{
			// Entry is a struct, so FirstOrDefault cannot answer "not found" - ask the index directly.
			var match = uint.TryParse(idText, out var id) ? entries.Where(e => e.Id == id).ToList() : [];
			if (match.Count == 0)
			{
				Console.WriteLine($"particle-effects: no RDB {ParticleEffectType} record with id '{idText}'.");
				return;
			}

			var entry = match[0];

			var text = System.Text.Encoding.Latin1.GetString(reader.ReadDecompressed(entry));
			var start = text.IndexOf("<?xml", StringComparison.Ordinal);
			if (start < 0)
			{
				Console.WriteLine($"{ParticleEffectType}:{id} is the COMPILED BINARY form, not authored XML - there is no source to print.");
				Console.WriteLine("Run the report with no argument to see which ids are authored.");
				return;
			}

			// The 12-byte RDB data header sits in front of the text; everything from `<?xml` on is the source.
			Console.WriteLine(text[start..]);
		}

		/// <summary>Whether any emitter loops indefinitely - i.e. the effect needs an explicit stop.</summary>
		private static string Looping(string text)
		{
			var forever = LoopCountMax.Matches(text)
				.Select(m => long.TryParse(m.Groups[1].Value.Trim(), out var v) ? v : 0)
				.Any(v => v >= LoopsForever);

			if (forever)
				return "loops";

			var longest = DurationMax.Matches(text)
				.Select(m => double.TryParse(m.Groups[1].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
				.DefaultIfEmpty(0)
				.Max();

			return longest > 0 ? $"{longest:0.#}s" : "once";
		}

		// The build path's tail is what names the effect; the `W:\achardb\conan\` prefix is the same on all of them.
		// These are WINDOWS paths being read on Linux, so `Path.GetFileName` does not split them - it treats the
		// whole thing as one name. Trim to the tail after `particles\` by hand, which also keeps the authoring
		// FOLDER (`roberth\Character\Spells\combos\`), and that folder is half of what names the effect.
		private static string ShortAsset(string path)
		{
			var idx = path.IndexOf(@"particles\", StringComparison.OrdinalIgnoreCase);
			return idx >= 0 ? path[(idx + 10)..] : path;
		}

		/// <summary>The authored source's path, trimmed to the part that names the effect. Null when it is blank.</summary>
		private static string? EffectName(string exportPath)
		{
			var trimmed = ShortAsset(exportPath.Trim());
			if (trimmed.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
				trimmed = trimmed[..^4];

			return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
		}

		/// <summary>
		///     Strips the name table's asset suffix. It carries the record's FORM, not part of its name, and the two
		///     forms partition the type exactly: 7,590 compiled records are named <c>*.particleeffect</c> and the 189
		///     authored ones <c>*.xml</c> - the same 189, set-for-set, that inflate to a <c>&lt;particlesystem&gt;</c>
		///     document. So the name table is not colliding with some other type that happens to share an id; it names
		///     both halves of this one.
		/// </summary>
		private static string TrimEffectSuffix(string name)
		{
			foreach (var suffix in (string[])[".particleeffect", ".xml"])
				if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					return name[..^suffix.Length];

			return name;
		}

		private sealed record Effect(uint Id, string? Name, string? AuthoredPath, List<string> Assets, string? Loop);
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Cli
{
	// `--report particle-effects [--report-arg <substring>]`: every RDB 1010207 particle effect, NAMED by the texture
	// assets its payload references (#880).
	//
	// ⚠️ **The catalog page recorded this payload as "an undecoded zlib blob (a binary effect graph, no strings)".**
	// The "no strings" half is the `pack_grep_lies` trap: the record IS zlib, so a grep over the pack file finds
	// nothing, and the strings only appear once it is inflated. Inflated, a typical effect carries the original
	// build-machine paths of the textures it draws with -
	// `W:\achardb\conan\particles\smoke\gradient_ab.png` and the like.
	//
	// That matters more than the graph does. The server never renders a particle effect - it sends an ID
	// (`SimpleDynelVisualChangeIIR e_StartParticleEffect`) and the client does the rest - so the useful question is
	// never "what does effect 3667002 look like" but "which id is the one I want". A texture path answers that; the
	// binary graph would not. #678 and #698 both need to pick an effect id, and this is how you find one.
	public static partial class ReportCli
	{
		private const uint ParticleEffectType = 1010207;

		// The build machine's own asset paths, as they survive in the payload.
		private static readonly Regex AssetPath = new(@"[A-Za-z]:\\[ -~]{4,120}?\.(?:png|tga|dds|jpg)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
			int named = 0, unreadable = 0, noAssets = 0;
			var rows = new List<(uint Id, List<string> Assets)>();

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
				if (assets.Count == 0)
				{
					noAssets++;
					continue;
				}

				named++;
				if (filter == null || assets.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase)))
					rows.Add((e.Id, assets));
			}

			Console.WriteLine($"particle-effects: {entries.Count:N0} records of RDB {ParticleEffectType}.");
			Console.WriteLine($"  {named:N0} carry at least one texture asset path, {noAssets:N0} carry none, {unreadable:N0} would not inflate.");
			if (filter != null)
				Console.WriteLine($"  filtered to assets containing \"{filter}\": {rows.Count:N0} record(s).");

			Console.WriteLine();
			foreach (var (id, assets) in rows.OrderBy(r => r.Id).Take(filter == null ? 40 : 400))
				Console.WriteLine($"  {ParticleEffectType}:{id,-10} {string.Join("  |  ", assets.Select(ShortAsset))}");

			if (filter == null && rows.Count > 40)
				Console.WriteLine($"  ... and {rows.Count - 40:N0} more - pass --report-arg <substring> to search by texture name.");
		}

		// The build path's tail is what names the effect; the `W:\achardb\conan\` prefix is the same on all of them.
		private static string ShortAsset(string path)
		{
			var idx = path.IndexOf(@"particles\", StringComparison.OrdinalIgnoreCase);
			return idx >= 0 ? path[(idx + 10)..] : path;
		}
	}
}

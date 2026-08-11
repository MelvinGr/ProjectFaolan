using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;

namespace Faolan.Importer.Cli
{
	// The three-way stat-name reconciliation (#625 item 4), printed by `--report ldb-stat-names`.
	//
	// A stat id can be named by THREE things and they do not agree:
	//
	//   ours   `CharacterStatId`                       - seeded years ago, partly from an Anarchy Online stat list
	//   LDB    category 615                            - the client's DISPLAY names, spaced and localisable
	//   EXE    the descriptor registry `FUN_01db2740`  - compiled with this build, recovered into
	//                                                    docs/re/aoc-client-stat-names.tsv
	//
	// ⭐ **The EXE wins when the two client sources disagree.** It is compiled with the build, it names ids the LDB
	// never reaches at all (the whole 1000000+ range), and a display string is exactly what goes stale when a stat
	// slot is repurposed. `0x0A9` is the worked example: LDB says `ChosenGod`, the exe says `HeroicMode`, and
	// `player_stats.heroic_mode` is a real field of the client's own protobuf schema while nothing named for a deity
	// is - Age of Conan has no player-choosable religion at all.
	//
	// #625 item 4 asks which of our disagreements are "a real decode bug worth finding" rather than the same thing
	// under a different word. This splits them:
	//
	//   NEAR   one is a rewording of the other (`PvpExp`/`PvPXP`, `Money`/`Cash`, `ProtectionCold`/`FrostResist`)
	//   FAR    they name DIFFERENT THINGS, and ours is the one to distrust
	//
	// ⚠️ The split is heuristic and is printed for a human to read, not acted on. What makes the FAR list worth
	// reading is its shape: several are unmistakably Anarchy Online terms - `AwardedOmniTekTokens` (OmniTek is an AO
	// faction) and `NanoInterval` (nano is AO's magic) - sitting on ids Age of Conan uses for something else. That
	// identifies the class: the enum was seeded from an AO stat list, and where AoC repurposed a slot our name stayed
	// behind.
	//
	// ⭐ And the same is true of LDB 615 itself, which is why the client-vs-client column is the more useful one: it
	// still says `Organization rank` (AO's word for a guild), `AMS`, `Bandolier Slots` and `Map Flags` on ids the
	// compiled registry calls `ClanLevel`, `SpellCategory`, `ItemSource` and `MaxConsciousness`. The display table
	// was never fully re-authored; the registry was. That is the reason the exe wins, rather than a preference.
	// See docs/re/aoc-client-stat-names.md.
	public static partial class ReportCli
	{
		// Word pairs that are the same concept spelled differently, plus qualifiers whose presence or absence does not
		// change what the stat IS. Applied both ways round.
		private static readonly (string A, string B)[] StatNameSynonyms =
		[
			("exp", "xp"), ("money", "cash"), ("pct", "percent"), ("regen", "regeneration"), ("dmg", "damage"),
			("mod", "modifier"), ("modifier", "multiplier"), ("crit", "critical"), ("resist", "resistance"),
			("resistance", "resist"), ("protection", "resist"), ("def", "defense"), ("cold", "frost"),
			("ooc", "outofcombat"), ("noncombat", "ooc"), ("curr", "current"), ("prev", "previous"),
			("combatrating", "damagemodifier"), ("attackrating", "specialization"), ("magicrating", "specialization")
		];

		private static void StatNameReconcile(IReadOnlyDictionary<uint, string> ldbNames)
		{
			var tsv = TsvStatNames();
			if (tsv.Count == 0)
			{
				Console.WriteLine();
				Console.WriteLine("(docs/re/aoc-client-stat-names.tsv not readable - skipping the three-way reconciliation.)");
				return;
			}

			var near = new List<string>();
			var far = new List<string>();
			var clientSplit = new List<string>();

			foreach (var id in Enum.GetValues<CharacterStatId>().Distinct())
			{
				var member = Enum.GetName(id)!;
				if (!tsv.TryGetValue((uint)id, out var exe))
					continue;

				// The two CLIENT sources disagreeing is its own finding, and the more interesting one: it says the id
				// was repurposed and only the compiled name kept up.
				if (ldbNames.TryGetValue((uint)id, out var ldb) && StatKey(ldb) != StatKey(exe))
					clientSplit.Add($"  0x{(uint)id:X4}  LDB '{ldb}'  vs  EXE '{exe}'   (ours: {member})");

				if (StatKey(member) == StatKey(exe))
					continue;

				var line = $"  {(uint)id,7} 0x{(uint)id:X4}  ours={member,-38} exe={exe}";
				(SameConcept(member, exe) ? near : far).Add(line);
			}

			Console.WriteLine();
			Console.WriteLine("--- three-way reconciliation: ours vs the EXE's descriptor registry (#625 item 4) ---");
			Console.WriteLine($"  {near.Count} near-synonym(s), {far.Count} that name DIFFERENT THINGS.");
			Console.WriteLine();
			Console.WriteLine("⭐ the FAR list - ours is the one to distrust. Several are unmistakably ANARCHY ONLINE terms");
			Console.WriteLine("   (`AwardedOmniTekTokens`, `NanoInterval`) on ids Age of Conan repurposed, which identifies the class:");
			foreach (var f in far)
				Console.WriteLine(f);

			Console.WriteLine();
			Console.WriteLine($"⚠️ and {clientSplit.Count} id(s) where the CLIENT'S OWN TWO SOURCES disagree - LDB 615 against the exe's");
			Console.WriteLine("   registry. The exe wins, and the LDB's own entries say why: it still carries `Organization rank`");
			Console.WriteLine("   (Anarchy Online\'s word for a guild), `AMS`, `Bandolier Slots` and `Map Flags` on ids the registry");
			Console.WriteLine("   calls `ClanLevel`, `SpellCategory`, `ItemSource` and `MaxConsciousness`. The DISPLAY table was never");
			Console.WriteLine("   fully re-authored; the compiled one was. `0x0A9` ChosenGod/HeroicMode is the worked example (#625).");
			foreach (var c in clientSplit.Take(40))
				Console.WriteLine(c);

			if (clientSplit.Count > 40)
				Console.WriteLine($"  ... and {clientSplit.Count - 40} more");
		}

		// Letters and digits only - our names are PascalCase and the client's are spaced or parenthesised.
		private static string StatKey(string name)
		{
			return Regex.Replace(name, "[^A-Za-z0-9]", "").ToLowerInvariant();
		}

		// "The same concept under a different word": one contains the other, or they match once a synonym pair is
		// applied. Deliberately generous - a false NEAR only hides a line from a list a human still reads, while a
		// false FAR sends someone chasing a rewording.
		private static bool SameConcept(string ours, string theirs)
		{
			var a = StatKey(ours);
			var b = StatKey(theirs);
			if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
				return true;

			foreach (var (x, y) in StatNameSynonyms)
			{
				if (a.Replace(x, y, StringComparison.Ordinal) == b.Replace(x, y, StringComparison.Ordinal))
					return true;
			}

			return false;
		}

		// The recovered exe table, as shipped in the repo - `statId <TAB> hex <TAB> clientName`, `#` comments.
		private static Dictionary<uint, string> TsvStatNames()
		{
			var names = new Dictionary<uint, string>();
			var path = FindUp(Path.Combine("docs", "re", "aoc-client-stat-names.tsv"));
			if (path == null)
				return names;

			foreach (var line in File.ReadLines(path))
			{
				if (line.StartsWith('#'))
					continue;

				var cells = line.Split('\t');
				if (cells.Length >= 3 && uint.TryParse(cells[0], out var id))
					names[id] = cells[2];
			}

			return names;
		}

		// The repo-relative path, found by walking UP from the working directory and then from the executable - the
		// same rule `Global.ConfigPath` uses for faolan.json, so the report works from anywhere in the tree rather
		// than only from the repo root.
		private static string? FindUp(string relative)
		{
			foreach (var start in (string[]) [Directory.GetCurrentDirectory(), AppContext.BaseDirectory])
			{
				for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
				{
					var candidate = Path.Combine(dir.FullName, relative);
					if (File.Exists(candidate))
						return candidate;
				}
			}

			return null;
		}
	}
}

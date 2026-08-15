using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Cli;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Per-NPC dialogue screens + mid-conversation quest grants per (capture, NPC) (#37).
		// Content coverage: of the NPCs/vendors we've actually INTERACTED with across the capture corpus, how many did
		// we import. Capture-relative by necessity (there is no authoritative game-wide vendor/dialogue list - retail
		// dialogue is TEA-encrypted in the RDB), so it measures import completeness OF WHAT WE'VE SEEN and names the
		// gaps to go capture/decode. `--report-arg readme` rewrites the marked section of README.md; else it prints.
		private static async Task Coverage(IReadOnlyList<NamedCapture> captures, string? arg, string? rdbDir)
		{
			// Conversations: greeted NPCs (denominator) vs imported ConversationNodes (numerator).
			var greeted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in captures)
				foreach (var n in ConversationExtractor.GreetedNpcNames(c.Packets))
					greeted.Add(n);

			// Shops: vendors whose 0x2033 window appears in a dump (denominator) vs imported Shop rows (numerator).
			var vendorsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var c in captures)
				foreach (var e in CaptureShopImporter.Extract(c.Packets))
					vendorsSeen.Add(e.VendorName);

			using var db = OpenDb();
			var convImported = (await db.ConversationNodes.Select(n => n.NpcName).Distinct().ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var shopImported = (await db.Shops.Select(s => s.VendorName).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

			// A greeted VENDOR opens its shop, not a conversation - exclude vendors (imported shops) from the dialogue
			// denominator so they don't read as conversation gaps. A real conversation gap = greeted, but neither a
			// dialogue node NOR a shop was imported for it (it falls to the "no dialogue or shop imported" bubble).
			var greetedTalkers = greeted.Where(n => !shopImported.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var convGaps = greetedTalkers.Where(n => !convImported.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
			var shopGaps = vendorsSeen.Where(v => !shopImported.Contains(v)).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
			var convHave = greetedTalkers.Count - convGaps.Count;
			var shopHave = vendorsSeen.Count - shopGaps.Count;

			var block = new StringBuilder();
			block.AppendLine($"- **NPC conversations** — {convHave}/{greetedTalkers.Count} greeted dialogue NPCs imported ({Pct(convHave, greetedTalkers.Count)})");
			block.AppendLine($"- **Vendor shops** — {shopHave}/{vendorsSeen.Count} captured vendors imported ({Pct(shopHave, vendorsSeen.Count)})");

			// RDB item/feat/ability template decryption (optional - needs the client RDB). The whole catalog (type
			// 1000020) is TEA-encrypted; we can only decrypt an entry whose key we've harvested from a capture (item
			// quads via ItemHashHarvester, ability keys via the 0x2052 grants). There is NO plaintext category tag in
			// the encrypted RDBH header, so a per-category %-of-total is impossible (we can't tell an undecrypted
			// entry's category) - we report the overall decrypted share + the absolute per-category decrypted counts.
			var rdb = string.IsNullOrEmpty(rdbDir)
				? RdbDir()
				: rdbDir;
			if (!string.IsNullOrEmpty(rdb) && Directory.Exists(rdb))
			{
				var itemIds = (await db.Items.Select(i => i.Id).ToListAsync()).ToHashSet();
				var abilityIds = (await db.Abilities.Select(a => a.Id).ToListAsync()).ToHashSet();
				using var reader = OpenRdb(rdb);
				var catalog = reader.EntriesOfType(1000020).ToList();
				int encrypted = 0, plaintext = 0, decrypted = 0, itemsDec = 0, abilitiesDec = 0;
				foreach (var e in catalog)
				{
					byte[] blob;
					try
					{
						blob = reader.ReadDecompressed(e);
					}
					catch
					{
						continue;
					}

					if (RdbReader.IsRdbh(blob))
					{
						encrypted++;
						if (itemIds.Contains(e.Id))
						{
							decrypted++;
							itemsDec++;
						}
						else if (abilityIds.Contains(e.Id))
						{
							decrypted++;
							abilitiesDec++;
						}
					}
					else
						plaintext++;
				}

				block.AppendLine(
					$"- **RDB template catalog** (type 1000020, TEA-encrypted) — {decrypted:N0}/{encrypted:N0} decrypted, **{Pct(decrypted, encrypted)} of the total encrypted**. Keys held per category: {itemsDec:N0} items, {abilitiesDec:N0} abilities, 0 feats (feat keys absent from captures). (+{plaintext:N0} plaintext catalog entries, no key needed.)");
			}

			Console.WriteLine("Content coverage (capture-relative: of the NPCs/vendors seen in dumps, how many are imported)\n");
			Console.Write(block.ToString());
			if (convGaps.Count > 0)
				Console.WriteLine($"\nconversation gaps ({convGaps.Count}) - greeted in a dump, no dialogue imported:\n  {string.Join(", ", convGaps)}");
			if (shopGaps.Count > 0)
				Console.WriteLine($"\nshop gaps ({shopGaps.Count}) - a vendor window is in a dump, no Shop row:\n  {string.Join(", ", shopGaps)}");

			// Imported-content stats: the scale of the DB (the server's source of truth). One `·`-separated line.
			var stats = $"**Imported content** (the DB is the server's source of truth): " +
						$"{await db.Maps.CountAsync():N0} maps · {await db.NpcTemplates.CountAsync():N0} NPC types / {await db.NpcSpawns.CountAsync():N0} placements · " +
						$"{await db.Items.CountAsync():N0} items · {await db.Spells.CountAsync():N0} spells · {await db.Abilities.CountAsync():N0} abilities · " +
						$"{await db.Feats.CountAsync():N0} feats · " +
						$"{await db.ConversationNodes.CountAsync():N0} conversation nodes · {await db.Shops.CountAsync():N0} shops · {await db.QuestDefinitions.CountAsync():N0} quests · " +
						$"{await db.WorldObjects.CountAsync():N0} world objects · {await db.PatrolNodes.Where(n => n.ParentPath == null).CountAsync():N0} patrol routes · " +
						$"{await db.MapRegions.CountAsync():N0} client trigger volumes";
			Console.WriteLine("\n" + stats);

			if (string.Equals(arg, "readme", StringComparison.OrdinalIgnoreCase))
			{
				UpdateReadmeSection("coverage", block.ToString().TrimEnd());
				UpdateReadmeSection("stats", stats);
				Console.WriteLine("\nREADME.md coverage + stats sections regenerated.");
			}

			return;

			static string Pct(int have, int total)
			{
				return total == 0
					? "n/a"
					: $"{100.0 * have / total:0.0}%";
			}
		}

		private static void ConversationStates(IReadOnlyList<NamedCapture> captures)
		{
			foreach (var c in captures)
				foreach (var g in ConversationExtractor.Extract(c.Packets).Where(e => !string.IsNullOrEmpty(e.NpcName)).GroupBy(e => e.NpcName).OrderBy(g => g.Key))
				{
					var grants = g.Where(e => e.GrantQuestId != 0).OrderBy(e => e.AnswerPath).Select(e => $"'{e.AnswerPath}'->{e.GrantQuestId}");
					Console.WriteLine($"{c.Name} | {g.Key} | screens={g.Count()} | grants=[{string.Join(", ", grants)}]");
				}
		}

		// The biggest 0x737A6DF9 FullChar snapshots across the captures: size, instance, and the byte offsets where
		// the 0x737A6DF9 subop recurs in the body (>1 => the "snapshot" is several records concatenated, not one).
		private static void SnapshotScan(IReadOnlyList<NamedCapture> captures)
		{
			var snaps = new List<(string Cap, byte[] Body)>();
			foreach (var c in captures)
				foreach (var p in c.Packets)
				{
					if (!p.IsDownload || p.SubOpcode != FullPlayerCharacterCodec.Opcode)
						continue;

					var body = p.Bytes.Length >= 16
						? p.Bytes[(12 + (int)p.Bytes.ReadU32Be(8))..]
						: [];
					if (body.Length >= 16)
						snaps.Add((c.Name, body));
				}

			foreach (var (cap, body) in snaps.OrderByDescending(s => s.Body.Length).Take(6))
			{
				var positions = new List<int>();
				for (var i = 0; i + 4 <= body.Length; i++)
				{
					if (body[i] == 0x73 && body[i + 1] == 0x7A && body[i + 2] == 0x6D && body[i + 3] == 0xF9)
						positions.Add(i);
				}

				var instance = body.ReadU32Be(12);
				Console.WriteLine(
					$"{cap[..Math.Min(46, cap.Length)],-46} size={body.Length,8} instance=0x{instance:X8} 0x{FullPlayerCharacterCodec.Opcode:X8}@[{string.Join(",", positions)}]");
				Console.WriteLine($"    first48: {Convert.ToHexString(body.AsSpan(0, Math.Min(48, body.Length)))}");

				try
				{
					var rootMsg = SnapshotRoot.Of(body.AsSpan(4)); // past the record's carrier length word
					Console.WriteLine($"    inventory-root: OK ({rootMsg.Inventories.Count} containers)");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"    inventory-root: FAIL - {ex.GetType().Name}: {ex.Message}");
				}
			}
		}

		private static void NpcGenericStats(IReadOnlyList<NamedCapture> captures)
		{
			var lengths = new Dictionary<int, int>();
			var samples = new List<string>();
			int spawns = 0, withStats = 0;

			foreach (var c in captures)
			foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
			{
				var body = e.Data is { Length: >= 12 }
					? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
					: null;
				if (body == null || !SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				spawns++;
				var stats = read.Message.GenericStats;
				if (stats is not { Length: > 0 })
					continue;

				withStats++;
				lengths[stats.Length] = lengths.GetValueOrDefault(stats.Length) + 1;
				if (samples.Count < 8)
					samples.Add($"{e.Name} (level {read.Message.Stats?.Level}): [{string.Join(", ", stats.Take(16))}]");
			}

			Console.WriteLine($"npc-generic-stats: {spawns} NPC spawns read, {withStats} carry generic_stats");
			foreach (var (length, n) in lengths.OrderByDescending(k => k.Value))
				Console.WriteLine($"  length {length}: {n}");

			foreach (var sample in samples)
				Console.WriteLine($"  {sample}");
		}
	}
}

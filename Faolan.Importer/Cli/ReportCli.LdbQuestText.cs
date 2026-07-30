using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report ldb-quest-text` - decrypt and print the quest text the client ships ENCRYPTED (#520).
	//
	// The eleven encrypted LDB categories are not readable from the RDB alone: the key is per string and the SERVER
	// sends it, so the readable set is exactly the strings we have captured a `key=` for. This report is the join -
	// ciphertext from the client's LDB pages, keys from our own tables - and it exists because the decode was otherwise
	// only reproducible by hand (dump the pages, parse, decrypt in a scratch script).
	//
	// It is the EVIDENCE PATH for anything typed off the objective text. A goal's kind tag (QuestGoalKind, the wire
	// `Separator`) and its objective text are two INDEPENDENT statements of what the goal asks for, so printing them
	// side by side is a cross-check: where they disagree, one of them is wrong or the kind value is unmapped. It is also
	// the only place a `Deliver` goal's RECIPIENT is written down - the row carries the item but never the target NPC.
	public static partial class ReportCli
	{
		// The quest half of the encrypted set. 50304 is `quest_goal_templates.goal_name` (the per-goal objective line);
		// the rest are the journal's quest-level slots, which QuestTextRef addresses by QuestTextSlot.
		private const uint GoalNameCategory = 50304;

		private static async Task LdbQuestText(string rdbDir, string? arg)
		{
			var wanted = ParseIdFilter(arg);
			var ciphertext = EncryptedLdbBlobs(rdbDir);
			if (ciphertext.Count == 0)
			{
				Console.WriteLine("ldb-quest-text: no ENGLISH encrypted LDB pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			await using var db = OpenDb();

			// AsNoTracking is load-bearing on a projection that pulls an OWNED type (the key): EF cannot build an
			// identity-resolved owner for it and throws at query time otherwise.
			var goals = await db
				.QuestGoalDefinitions.AsNoTracking()
				.OrderBy(g => g.QuestDefinitionId)
				.ThenBy(g => g.RegionOrder)
				.Select(g => new
				{
					g.GoalId,
					g.QuestDefinitionId,
					g.ObjectiveCategory,
					g.ObjectiveRefId,
					g.ObjectiveKey,
					g.Separator,
					g.GoalType,
					g.TargetName,
					g.RequiredCount
				})
				.ToListAsync();

			if (wanted.Count > 0)
				goals = goals.Where(g => wanted.Contains(g.GoalId) || wanted.Contains(g.QuestDefinitionId)).ToList();

			// Scoped to the quests the goal filter left, so `--report-arg <goal>` prints that goal's own journal entry
			// rather than the whole catalog's.
			var titles = await QuestTitles(db, ciphertext, wanted.Count == 0
				? null
				: goals.Select(g => g.QuestDefinitionId).ToHashSet());

			Console.WriteLine($"=== quest goal objective text ({goals.Count} goals) ===");
			Console.WriteLine("goal\tquest\tkind\tgoalType\tcount\ttarget\tobjective text");

			int decrypted = 0, noKey = 0, noCipher = 0, failed = 0;
			foreach (var g in goals)
			{
				var category = g.ObjectiveCategory == 0
					? GoalNameCategory
					: g.ObjectiveCategory;

				string text;
				if (g.ObjectiveKey.IsEmpty)
				{
					text = "(no key captured)";
					noKey++;
				}
				else if (!ciphertext.TryGetValue((category, g.ObjectiveRefId), out var blob))
				{
					text = $"(no LDB[{category}][{g.ObjectiveRefId}] on an English page)";
					noCipher++;
				}
				else
				{
					var plain = LdbKey.Decrypt(blob, g.ObjectiveKey);
					if (plain == null)
					{
						text = "(DECRYPT FAILED - key does not match this ciphertext)";
						failed++;
					}
					else
					{
						text = plain;
						decrypted++;
					}
				}

				var kind = Enum.IsDefined(typeof(QuestGoalKind), g.Separator)
					? ((QuestGoalKind)g.Separator).ToString()
					: $"?{g.Separator}";

				Console.WriteLine(
					$"{g.GoalId}\t{g.QuestDefinitionId}\t{kind}\t{g.GoalType}\t{g.RequiredCount}\t{g.TargetName ?? "-"}\t{Flatten(text)}");
			}

			Console.WriteLine();
			Console.WriteLine($"decrypted {decrypted}/{goals.Count}; {noKey} with no captured key, {noCipher} absent from the English pages, {failed} FAILED");

			// A failure is the interesting outcome: the key and the ciphertext are independent sources, so a mismatch
			// means one of them is wrong (a re-harvest that crossed keys, or the wrong page picked).
			if (failed > 0)
				Console.WriteLine("⚠️  a DECRYPT FAILED row means the stored key and the shipped ciphertext disagree - not a padding artefact.");

			if (titles.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"=== quest-level text slots ({titles.Count} quests) ===");
			foreach (var (questId, slots) in titles.OrderBy(kv => kv.Key))
			{
				foreach (var (slot, text) in slots.OrderBy(s => s.Slot))
					Console.WriteLine($"q{questId}\t{slot}\t{Flatten(text)}");
			}
		}

		// Every quest's decryptable quest-level slots (title, summary, solved text, ...), keyed by quest id. Only slots
		// whose category is one of the encrypted ones AND whose key decrypts are included - a plaintext slot is already
		// readable elsewhere and a keyless one has nothing to say here.
		private static async Task<Dictionary<uint, List<(QuestTextSlot Slot, string Text)>>> QuestTitles(
			Faolan.Core.Database.DatabaseContext db, Dictionary<(uint Category, uint Id), byte[]> ciphertext,
			HashSet<uint>? onlyQuests)
		{
			var refs = await db
				.Set<QuestTextRef>().AsNoTracking()
				.Select(r => new { r.QuestId, r.Slot, r.Category, r.RefId, r.KeyText })
				.ToListAsync();

			var byQuest = new Dictionary<uint, List<(QuestTextSlot, string)>>();
			foreach (var r in refs)
			{
				if (onlyQuests != null && !onlyQuests.Contains(r.QuestId))
					continue;

				if (r.KeyText.IsEmpty || !ciphertext.TryGetValue((r.Category, r.RefId), out var blob))
					continue;

				var plain = LdbKey.Decrypt(blob, r.KeyText);
				if (plain == null)
					continue;

				if (!byQuest.TryGetValue(r.QuestId, out var slots))
					byQuest[r.QuestId] = slots = [];
				slots.Add((r.Slot, plain));
			}

			return byQuest;
		}

		// (category, id) -> ciphertext, over the ENGLISH pages of the ENCRYPTED categories only.
		//
		// Both filters matter. The language comes from the client's own language records (LdbLanguageIndex) rather than
		// from the text, because whole pages ship untranslated and a text sniffer therefore unions several languages and
		// silently yields French. And the encryption flag is the page header's own `flags` word, so the encrypted set is
		// the page's statement rather than something inferred from how the bytes look.
		private static Dictionary<(uint Category, uint Id), byte[]> EncryptedLdbBlobs(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var blobs = new Dictionary<(uint, uint), byte[]>();

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(x => x.IsLocal))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not a readable record (CDN-only / not zlib)
				}

				if (LdbStringIndex.CategoryOf(body) == 0 || LdbStringIndex.FlagsOf(body) != 1)
					continue; // not a TDC2 page, or a plaintext one

				foreach (var entry in LdbStringIndex.ParseRaw(body))
					blobs[(entry.Category, entry.Id)] = entry.Bytes;
			}

			return blobs;
		}

		// `--report-arg` as a comma-separated list of goal ids and/or quest ids; empty = every goal.
		private static HashSet<uint> ParseIdFilter(string? arg)
		{
			if (string.IsNullOrWhiteSpace(arg))
				return [];

			return arg
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Select(s => uint.TryParse(s, out var u)
					? u
					: 0u)
				.Where(u => u != 0)
				.ToHashSet();
		}

		// One line per record: the dialogue half carries newlines and the journal summaries are paragraphs, and this
		// output is TSV.
		private static string Flatten(string text)
		{
			return text.Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", "  ");
		}
	}
}

using System;
using System.Collections.Generic;
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
	//
	// A PARTIAL goal prints `(partial)` instead of a type and count. Those columns default to Kill/0 on a row the wire
	// never typed (QuestGoalDefinition.Partial), and printing the defaults here would read as a decoded fact - in an
	// output whose whole purpose is to be the evidence other typings are taken from.
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
					g.Partial,
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
					$"{g.GoalId}\t{g.QuestDefinitionId}\t{kind}\t{(g.Partial ? "(partial)" : g.GoalType.ToString())}\t{(g.Partial ? "-" : g.RequiredCount.ToString())}\t{g.TargetName ?? "-"}\t{Flatten(text)}");
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

		// `--report ldb-dialogue [--report-arg <npc name>]` - the NPC half of the encrypted set (categories 50000-50003).
		//
		// Same join as the quest half: ciphertext from the client's LDB pages, keys from our own tables. A conversation
		// SCREEN carries its own (Category, ScreenId, Key1..4), so each line of dialogue decrypts independently.
		//
		// Ordered by the node's ANSWER PATH, because that is the shape of the conversation: "" is the greeting, and each
		// deeper path is what the NPC says after the option the player picked. Reading a whole NPC in path order is how
		// you find a specific beat - a farewell, a hand-off, a quest grant - rather than guessing which screen it is.
		private static async Task LdbDialogue(string rdbDir, string? npcFilter)
		{
			var ciphertext = EncryptedLdbBlobs(rdbDir);
			if (ciphertext.Count == 0)
			{
				Console.WriteLine("ldb-dialogue: no ENGLISH encrypted LDB pages found - is RdbDir pointing at the client's RDB folder?");
				return;
			}

			await using var db = OpenDb();

			var nodes = await db
				.Set<ConversationNode>().AsNoTracking()
				.Where(n => npcFilter == null || n.NpcName == npcFilter)
				.Select(n => new { n.Id, n.NpcName, n.TypeId, n.AnswerPath, n.ConditionQuestId, n.ConditionQuestState, n.GrantQuestId })
				.ToListAsync();

			if (nodes.Count == 0)
			{
				Console.WriteLine($"ldb-dialogue: no conversation nodes for '{npcFilter}'.");
				return;
			}

			var ids = nodes.Select(n => n.Id).ToHashSet();
			var screens = await db
				.Set<ConversationScreen>().AsNoTracking()
				.Where(s => ids.Contains(s.ConversationNodeId))
				.Select(s => new { s.ConversationNodeId, s.PacketSequence, s.Sequence, s.Category, s.ScreenId, s.Key })
				.ToListAsync();

			var byNode = screens.ToLookup(s => s.ConversationNodeId);

			Console.WriteLine($"=== dialogue: {nodes.Count} node(s), {screens.Count} screen(s) ===");
			int ok = 0, failed = 0;
			foreach (var node in nodes.OrderBy(n => n.NpcName).ThenBy(n => n.AnswerPath.Length).ThenBy(n => n.AnswerPath))
			{
				// The gate is the interesting part on a quest NPC: a screen served only while quest N is in state S is
				// how retail scripts a hand-off, so print it beside the path rather than leaving it in the DB.
				var gate = node.ConditionQuestId is { } q
					? $"  [if quest {q} state {node.ConditionQuestState}]"
					: "";
				var grant = node.GrantQuestId is { } g
					? $"  [grants quest {g}]"
					: "";
				Console.WriteLine($"\n-- {node.NpcName}  type={node.TypeId}  path='{node.AnswerPath}'{gate}{grant}");

				foreach (var s in byNode[node.Id].OrderBy(s => s.PacketSequence).ThenBy(s => s.Sequence))
				{
					string text;
					if (s.Key.IsEmpty)
						text = "(no key captured)";
					else if (!ciphertext.TryGetValue((s.Category, s.ScreenId), out var blob))
						text = $"(no LDB[{s.Category}][{s.ScreenId}] on an English page)";
					else
					{
						var plain = LdbKey.Decrypt(blob, s.Key);
						if (plain == null)
						{
							text = "(DECRYPT FAILED)";
							failed++;
						}
						else
						{
							text = plain;
							ok++;
						}
					}

					Console.WriteLine($"   [{s.PacketSequence}.{s.Sequence}] cat {s.Category} id {s.ScreenId}: {Flatten(text)}");
				}
			}

			Console.WriteLine($"\ndecrypted {ok}/{screens.Count}; {failed} FAILED");
		}

		// `--report ldb-decrypt --report-arg <category>:<id>:<wire key>` - decrypt ONE encrypted LDB string from a key
		// supplied on the command line rather than from our tables.
		//
		// This is the ad-hoc counterpart to the two reports above, and it exists because the readable set is bounded by
		// what we have HARVESTED, not by what we can decrypt: find a `<remoteformat id=.. category=.. key=..>` in a
		// capture and you can read it immediately, without first teaching the importer to store it. That is how the
		// screens we do not hold get identified in the first place.
		//
		// The key is taken in its WIRE form, escaped exactly as it appears in the XML attribute - LdbKey.Decode
		// unescapes it - so it can be pasted straight out of a capture with no hand-editing.
		private static void LdbDecryptOne(string rdbDir, string? arg)
		{
			// SEVERAL triples may be given, separated by '='. Reading a whole conversation out of a capture is the normal
			// case, and the LDB pages are parsed ONCE for the batch rather than per string.
			//
			// ⚠️ The separator has to survive BOTH the alphabet and the XML ESCAPING. Only ':;<=>' are outside the base-85
			// alphabet, which rules out ',' - but ';' is no good either, because it TERMINATES an entity and a wire key is
			// escaped (`&amp;`, `&quot;`, `&#92;`): splitting on ';' tears `H1w&amp;.6T...` into pieces. ':' is already
			// the field separator inside a triple, so '=' it is.
			var entries = (arg ?? "").Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (entries.Length == 0)
			{
				Console.WriteLine("ldb-decrypt: --report-arg <category>:<id>:<wire key>[=<category>:<id>:<wire key>...]");
				return;
			}

			var ciphertext = EncryptedLdbBlobs(rdbDir);
			foreach (var entry in entries)
			{
				var parts = entry.Split(':', 3);
				if (parts.Length != 3 || !uint.TryParse(parts[0], out var category) || !uint.TryParse(parts[1], out var id))
				{
					Console.WriteLine($"  '{entry}': expected <category>:<id>:<wire key>");
					continue;
				}

				var key = LdbKey.Decode(parts[2]);
				if (key == null)
				{
					Console.WriteLine($"  LDB[{category}][{id}]: '{parts[2]}' is not a well-formed key (it must unescape to exactly {LdbKey.KeyLength} characters from the base-85 alphabet).");
					continue;
				}

				if (!ciphertext.TryGetValue((category, id), out var blob))
				{
					Console.WriteLine($"  LDB[{category}][{id}]: not on any ENGLISH encrypted page ({ciphertext.Count} strings loaded).");
					continue;
				}

				var plain = LdbKey.Decrypt(blob, key);
				Console.WriteLine(plain == null
					? $"  LDB[{category}][{id}]: did NOT decrypt with that key - they belong to different strings."
					: $"  LDB[{category}][{id}] = {plain}");
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
		internal static Dictionary<(uint Category, uint Id), byte[]> EncryptedLdbBlobsFor(string rdbDir) => EncryptedLdbBlobs(rdbDir);

		// (category, id) -> the FINISHED string, over the English pages of the PLAINTEXT categories - the other half of
		// the LDB, and the half nothing here used to read.
		//
		// Roughly half of the 47 categories ship unencrypted (docs/rdb/ldb-string-index.md names them all): 52000
		// regions, 51200 bounded_areas, 51000 npc_template, 50200/50201 item, 55000 simpledynels, ... A remoteformat
		// pointing at one of those carries no key and needs none, so resolving it through the ciphertext map can only
		// fail - which is why the quest journal's area header printed LDB[52000][4010] instead of its region name
		// (#561).
		//
		// Kept as its OWN walk rather than folded into EncryptedLdbBlobs, which would hand every existing caller a
		// second dictionary it does not want: the plaintext side includes the item-name category, ~100k strings, and
		// --report ldb-quest-text has no use for them. The cost is one extra pass for the one report that wants both.
		internal static Dictionary<(uint Category, uint Id), string> PlaintextLdbStringsFor(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var strings = new Dictionary<(uint, uint), string>();

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

				if (LdbStringIndex.CategoryOf(body) == 0 || LdbStringIndex.FlagsOf(body) == 1)
					continue; // not a TDC2 page, or an ENCRYPTED one (EncryptedLdbBlobs owns those)

				foreach (var entry in LdbStringIndex.Parse(body))
					strings[(entry.Category, entry.Id)] = entry.Value;
			}

			return strings;
		}

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

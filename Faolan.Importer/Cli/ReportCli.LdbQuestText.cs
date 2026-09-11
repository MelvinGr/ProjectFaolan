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
	// `--report ldb-quest-text` - decrypt and print the quest text the client ships encrypted.
	//
	// The encrypted LDB categories are not readable from the RDB alone: the key is per string and the server sends
	// it, so the readable set is exactly the strings a capture has shown a `key=` for. This report is the join -
	// ciphertext from the client's LDB pages, keys from the local tables.
	//
	// It is the evidence path for anything typed off the objective text. A goal's kind tag (QuestGoalKind, the wire
	// `Separator`) and its objective text are two independent statements of what the goal asks for, so printing them
	// side by side is a cross-check: where they disagree, one of them is wrong or the kind value is unmapped. It is
	// also the only place a `Deliver` goal's recipient is written down - the row carries the item but never the
	// target NPC.
	//
	// A partial goal prints `(partial)` instead of a type and count. Those columns default to Kill/0 on a row the
	// wire never typed (QuestGoalDefinition.Partial), and printing the defaults here would read as a decoded fact -
	// in an output whose whole purpose is to be the evidence other typings are taken from.
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
					g.RequiredCount,
					g.Name
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

			// The cross-check. The client's quest catalog (RDB 1000104) carries `QuestGoal.name` in plaintext, and the
			// encrypted LDB category 50304 is `quest_goal_templates.goal_name` - the same field, reached two entirely
			// independent ways: a protobuf data table on disk, versus a ciphertext page unlocked by a key the server
			// sent. Where both exist they must be identical, and a disagreement means one of the two paths is wrong
			// (a crossed key, the wrong page, a bad catalog dedup). Cheap to check, so check it every run.
			int agree = 0, catalogOnly = 0;
			var disagree = new List<string>();
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

				// null = the goal has never been on the wire (a catalog-only goal), so it has no kind - distinct from
				// a kind the corpus has not mapped, which prints as `?<value>`.
				// Where no key was captured the catalog name is the objective text - proven identical on every goal
				// where both are available - so print it rather than "(no key captured)", marked `[catalog]` so the
				// provenance of a line this report is used as evidence for stays visible.
				if (g.ObjectiveKey.IsEmpty && g.Name != null)
				{
					text = g.Name;
					catalogOnly++;
				}
				else if (g.Name != null && !text.StartsWith('('))
				{
					if (g.Name == text)
						agree++;
					else
						disagree.Add($"goal {g.GoalId} (quest {g.QuestDefinitionId})\n      catalog: {g.Name}\n      ldb    : {text}");
				}

				var kind = g.Separator is not { } sep
					? "-"
					: Enum.IsDefined(typeof(QuestGoalKind), sep)
						? ((QuestGoalKind)sep).ToString()
						: $"?{sep}";

				var source = g.ObjectiveKey.IsEmpty && g.Name != null
					? "[catalog] "
					: "";

				Console.WriteLine(
					$"{g.GoalId}\t{g.QuestDefinitionId}\t{kind}\t{(g.Partial ? "(partial)" : g.GoalType.ToString())}\t{(g.Partial ? "-" : g.RequiredCount.ToString())}\t{g.TargetName ?? "-"}\t{source}{Flatten(text)}");
			}

			Console.WriteLine();
			Console.WriteLine($"decrypted {decrypted}/{goals.Count}; {noKey} with no captured key, {noCipher} absent from the English pages, {failed} FAILED");
			Console.WriteLine($"catalog name used for {catalogOnly} goal(s) with no captured key");
			Console.WriteLine($"cross-check vs the client's quest catalog: {agree} agree, {disagree.Count} DIFFER");
			if (disagree.Count > 0)
			{
				Console.WriteLine("⚠️  the catalog and the decrypted LDB disagree - they are the same field reached two ways, so one path is WRONG:");
				foreach (var d in disagree)
					Console.WriteLine($"    {d}");
			}

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
		// Same join as the quest half: ciphertext from the client's LDB pages, keys from the local tables. A
		// conversation screen carries its own (Category, ScreenId, Key1..4), so each line decrypts independently.
		//
		// Walked in the shape of the conversation itself: each root first, then breadth-first along the option edges,
		// with every screen indented by how deep it is and labelled with the option that reaches it. Reading a whole
		// NPC this way is how a specific beat is found - a farewell, a hand-off, a quest grant - rather than guessing
		// which screen it is. A screen already printed is named again but not re-expanded, so the genuine loops in the
		// corpus (Bhaltair, Guard Captain Marcus, Hadrathus) show as loops instead of running forever.
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
				.Select(n => new { n.Id, n.NpcName, n.ContentKey, n.Discriminator })
				.ToListAsync();

			if (nodes.Count == 0)
			{
				Console.WriteLine($"ldb-dialogue: no conversation screens for '{npcFilter}'.");
				return;
			}

			var nodeIds = nodes.Select(n => n.Id).ToHashSet();
			var roots = await db
				.Set<ConversationEntry>().AsNoTracking()
				.Where(e => nodeIds.Contains(e.NodeId))
				.Select(e => new { e.NpcName, e.Trigger, e.TriggerWorldObjectId, e.ConditionQuestId, e.ConditionQuestState, e.NodeId })
				.ToListAsync();
			var edges = (await db
					.Set<ConversationEdge>().AsNoTracking()
					.Where(e => nodeIds.Contains(e.FromNodeId))
					.Select(e => new { e.FromNodeId, e.OptionId, e.ToNodeId, e.ConditionQuestId, e.ConditionQuestState, e.GrantQuestId })
					.ToListAsync())
				.ToLookup(e => e.FromNodeId);

			var ids = nodes.Select(n => n.Id).ToHashSet();
			var screens = await db
				.Set<ConversationScreen>().AsNoTracking()
				.Where(s => ids.Contains(s.ConversationNodeId))
				.Select(s => new { s.ConversationNodeId, s.PacketSequence, s.Sequence, s.Category, s.ScreenId, s.Key })
				.ToListAsync();

			var byNode = screens.ToLookup(s => s.ConversationNodeId);

			Console.WriteLine($"=== dialogue: {nodes.Count} screen(s), {roots.Count} root(s), {edges.SelectMany(g => g).Count()} branch(es), {screens.Count} string(s) ===");
			int ok = 0, failed = 0;
			var names = nodes.ToDictionary(n => n.Id, n => n.NpcName);
			foreach (var root in roots.OrderBy(r => r.NpcName, StringComparer.Ordinal).ThenBy(r => r.Trigger).ThenBy(r => r.ConditionQuestId))
			{
				var gate = root.ConditionQuestId is { } q
					? $"  [if quest {q} state {root.ConditionQuestState}]"
					: "";
				var trigger = root.TriggerWorldObjectId is { } w
					? $" (object {w})"
					: "";
				Console.WriteLine($"\n== {root.NpcName}  {root.Trigger}{trigger}{gate}");

				// Breadth-first, so the option list of a screen is printed together rather than interleaved with a
				// depth-first dive down its first branch.
				var seen = new HashSet<uint>();
				var queue = new Queue<(uint Node, int Depth, string Label)>();
				queue.Enqueue((root.NodeId, 0, ""));
				while (queue.Count > 0)
				{
					var (nodeId, depth, label) = queue.Dequeue();
					var indent = new string(' ', 2 + depth * 2);
					if (!seen.Add(nodeId))
					{
						Console.WriteLine($"{indent}{label}-> 0x{nodeId:X8} (already shown - the menu loops back here)");
						continue;
					}

					Console.WriteLine($"{indent}{label}screen 0x{nodeId:X8}");
					foreach (var s in byNode[nodeId].OrderBy(s => s.PacketSequence).ThenBy(s => s.Sequence))
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

						Console.WriteLine($"{indent}  [{s.PacketSequence}.{s.Sequence}] cat {s.Category} id {s.ScreenId}: {Flatten(text)}");
					}

					foreach (var e in edges[nodeId].OrderBy(e => e.OptionId).ThenBy(e => e.ConditionQuestId))
					{
						var branchGate = e.ConditionQuestId is { } bq
							? $" [if quest {bq} state {e.ConditionQuestState}]"
							: "";
						var grant = e.GrantQuestId is { } g
							? $" [grants quest {g}]"
							: "";
						queue.Enqueue((e.ToNodeId, depth + 1, $"option {e.OptionId}{branchGate}{grant} "));
					}
				}
			}

			// A screen no root reaches is unreachable in game, which the answer-path tree could not even express.
			var reachable = new HashSet<uint>(roots.Select(r => r.NodeId));
			foreach (var e in edges.SelectMany(g => g))
				reachable.Add(e.ToNodeId);
			foreach (var orphan in nodes.Where(n => !reachable.Contains(n.Id)).OrderBy(n => n.NpcName, StringComparer.Ordinal))
				Console.WriteLine($"\n!! {orphan.NpcName} screen 0x{orphan.Id:X8} has no root and no inbound branch - unreachable ({orphan.ContentKey})");

			Console.WriteLine($"\ndecrypted {ok}/{screens.Count}; {failed} FAILED; {names.Count} screen(s) held");
		}

		// `--report ldb-decrypt --report-arg <category>:<id>:<wire key>` - decrypt one encrypted LDB string from a key
		// supplied on the command line rather than from our tables.
		//
		// This is the ad-hoc counterpart to the two reports above, and it exists because the readable set is bounded
		// by what has been harvested, not by what can be decrypted: a `<remoteformat id=.. category=.. key=..>` found
		// in a capture is readable immediately, without first teaching the importer to store it. That is how the
		// screens that are not held get identified in the first place.
		//
		// The key is taken in its wire form, escaped exactly as it appears in the XML attribute - LdbKey.Decode
		// unescapes it - so it can be pasted straight out of a capture with no hand-editing.
		private static void LdbDecryptOne(string rdbDir, string? arg)
		{
			// several triples may be given, separated by '='. Reading a whole conversation out of a capture is the normal
			// case, and the LDB pages are parsed once for the batch rather than per string.
			//
			// The separator has to survive both the alphabet and the XML escaping. Only ':;<=>' are outside the base-85
			// alphabet, which rules out ',' - but ';' is no good either, because it terminates an entity and a wire key is
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

		// Every quest's decryptable quest-level slots (title, summary, solved text,...), keyed by quest id. Only slots
		// whose category is one of the encrypted ones and whose key decrypts are included - a plaintext slot is already
		// readable elsewhere and a keyless one has nothing to say here.
		private static async Task<Dictionary<uint, List<(QuestTextSlot Slot, string Text)>>> QuestTitles(
			Core.Database.DatabaseContext db, Dictionary<(uint Category, uint Id), byte[]> ciphertext,
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

		// (category, id) -> ciphertext, over the english pages of the encrypted categories only.
		//
		// Both filters matter. The language comes from the client's own language records (LdbLanguageIndex) rather than
		// from the text, because whole pages ship untranslated and a text sniffer therefore unions several languages and
		// silently yields French. And the encryption flag is the page header's own `flags` word, so the encrypted set is
		// the page's statement rather than something inferred from how the bytes look.
		internal static Dictionary<(uint Category, uint Id), byte[]> EncryptedLdbBlobsFor(string rdbDir) => EncryptedLdbBlobs(rdbDir);

		// (category, id) -> the finished string, over the English pages of the plaintext categories - the other half
		// of the LDB.
		//
		// Roughly half of the categories ship unencrypted (docs/rdb/ldb-string-index.md names them all): 52000
		// regions, 51200 bounded_areas, 51000 npc_template, 50200/50201 item, 55000 simpledynels, ... A remoteformat
		// pointing at one of those carries no key and needs none, so resolving it through the ciphertext map can only
		// fail, and the reference prints as a bare `LDB[cat][id]` placeholder.
		//
		// Kept as its own walk rather than folded into EncryptedLdbBlobs, which would hand every existing caller a
		// second dictionary it does not want: the plaintext side includes the item-name category, ~100k strings, and
		// --report ldb-quest-text has no use for them. The cost is one extra pass for the one report that wants both.
		//
		// The walk itself lives on LdbStringIndex, because the importer wants it too (RdbQuestGoalTargetImporter) and
		// this is the RDB grammar's own concern, not a report's.
		internal static Dictionary<(uint Category, uint Id), string> PlaintextLdbStringsFor(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			return LdbStringIndex.EnglishPlaintext(rdb);
		}

		private static Dictionary<(uint Category, uint Id), byte[]> EncryptedLdbBlobs(string rdbDir)
		{
			using var rdb = OpenRdb(rdbDir);
			return LdbStringIndex.EnglishCiphertext(rdb);
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

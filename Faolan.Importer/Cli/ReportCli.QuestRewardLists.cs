using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Importer.Captures;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report quest-reward-lists` (#893): what the FOUR reward lists of a `Quest_c` record mean relative to each
	// other, checked against what retail actually HANDED OUT.
	//
	// The client's journal renderer (`FUN_01617260`, the QuestJournal view) settles the shape from the other side:
	//
	//     if (this quest is the LAST step of its line)      // line.quests.last() == quest.questId
	//         show XP @+0x28, money @+0x20, obj1 -> reward view, obj2 -> OPTIONAL reward view (+1)
	//     else
	//         show XP @+0xf0, money @+0xf8, obj3 -> reward view, obj4 -> OPTIONAL reward view (+1)
	//
	// (`FUN_016181f0` fills the first list it is handed into the reward view and the second into the "optional"
	// view, showing the `RewardPlusOne` marker iff that second list is non-empty.) So the record carries TWO reward
	// sets - one displayed on a line's last step, one on every other step - and within each, a GUARANTEED list and a
	// CHOOSE-ONE list.
	//
	// This report asks the corpus whether retail's behaviour matches that reading:
	//   * the pick-one-of-N OFFER (`QuestRewardNotifyIIR`, #815) should draw its options from obj2 or obj4;
	//   * the items GRANTED at completion (`DispatchAddItem` right after the QuestIIR state-4) should come from obj1
	//     or obj3 (and the picked option);
	//   * the same quest seen by characters of DIFFERENT classes should carry the same lists if nothing is
	//     class-specific - or differ if a list is.
	// Exit code is 0 always - a measurement.
	public static partial class ReportCli
	{
		// How far after a completion a DispatchAddItem is still "the completion's grant". The offer + confirm + grant
		// land inside one burst in the #815 capture; this is generous so a slow client does not lose one.
		private const int GrantWindowPackets = 400;

		private sealed record RewardSets(uint QuestId, IReadOnlyList<uint> Obj1, IReadOnlyList<uint> Obj2, IReadOnlyList<uint> Obj3, IReadOnlyList<uint> Obj4)
		{
			public string Membership(uint template)
			{
				var m = new List<string>();
				if (Obj1.Contains(template)) m.Add("obj1");
				if (Obj2.Contains(template)) m.Add("obj2");
				if (Obj3.Contains(template)) m.Add("obj3");
				if (Obj4.Contains(template)) m.Add("obj4");
				return m.Count == 0 ? "NONE" : string.Join("+", m);
			}
		}

		private static async Task<int> QuestRewardLists(IReadOnlyList<NamedCapture> captures)
		{
			await using var db = OpenDb();
			var lastOfLine = await db.QuestLines.AsNoTracking().Where(l => l.LastQuestId != 0).Select(l => l.LastQuestId).ToHashSetAsync();
			var lineOf = await db.QuestDefinitions.AsNoTracking().ToDictionaryAsync(q => q.QuestId, q => q.QuestLineId);
			var names = await db.Items.AsNoTracking().Where(i => i.Name != "").ToDictionaryAsync(i => i.Id, i => i.Name);
			// The client catalog's per-LINE reward (RDB 1000104 MainQuest.reward / optional_reward, imported as
			// QuestLineReward): the guaranteed list and the choose-one list, per line.
			var catalogRewards = (await db.QuestLineRewards.AsNoTracking().ToListAsync())
				.GroupBy(r => r.QuestLineId)
				.ToDictionary(g => g.Key, g => (Guaranteed: g.Where(r => !r.Optional).Select(r => r.ItemTemplateId).OrderBy(x => x).ToList(),
					Optional: g.Where(r => r.Optional).Select(r => r.ItemTemplateId).OrderBy(x => x).ToList()));
			int bMatchesCatalog = 0, bMismatch = 0, bNoCatalog = 0, aMatchesCatalog = 0, aMismatch = 0, aNoCatalog = 0;
			// Can the line half be BUILT? Its element words beyond the template: `level` should be the item's own
			// (Items.Level) and `quantity` should be constant per (line, template) if it is line data.
			var itemLevel = await db.Items.AsNoTracking().Where(i => i.Level != 0).ToDictionaryAsync(i => i.Id, i => (uint)i.Level);
			int levelAgree = 0, levelDisagree = 0, levelUnknown = 0;
			var quantityByLineTemplate = new Dictionary<(uint, uint), HashSet<uint>>();
			var quantityByTemplate = new Dictionary<uint, HashSet<uint>>();
			var mismatches = new List<string>();

			// Per (quest, class): the four lists as seen in that capture. A quest's lists come from the login snapshot
			// AND from the live QuestFullUpdate, both of which carry the same Quest_c record.
			var byQuestClass = new Dictionary<(uint Quest, ConanClass Class), RewardSets>();
			// Per distinct quest: which reward SET it populates (A = obj1/obj2, B = obj3/obj4, both, neither).
			var setOf = new Dictionary<uint, string>();
			// ⭐ THE CLIENT'S OWN PREDICATE, evaluated per record: MainQuest_c holds the area's (questId, sequence)
			// list AS THE SERVER SENT IT (the index region's block at login, the QuestJournalIIR's area block live),
			// and the journal renders set A iff this quest is that list's LAST entry. Scored per record against which
			// set the record populates; if the server fills the set its own client will display, the diagonal is full.
			var clientPredicate = new Dictionary<(string Set, bool Last), int>();
			var offDiagonal = new Dictionary<(uint Quest, string Set, uint SentLast), int>();
			// Per line: every captured quest's slots side by side, to test whether set B repeats across a line's
			// steps (a preview of ONE line reward) or differs per step (each step's own reward).
			var perLine = new Dictionary<uint, Dictionary<uint, (string Slots, bool Last)>>();
			// WITHIN ONE SNAPSHOT: for a line the character holds two or more steps of, do the steps' A slots agree?
			// do their B slots agree? A preview of one line reward agrees down the line; a per-step reward does not.
			int aSame = 0, aDiff = 0, bSame = 0, bDiff = 0;
			// Per quest: every distinct (A xp, A money, B xp, B money) the corpus carried, with counts.
			var slotVariants = new Dictionary<uint, Dictionary<string, int>>();
			var lineCountVsCatalog = new List<(uint Line, int Sent, int Catalog)>();
			var catalogSize = lineOf.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Count());
			// Every distinct (lists) variant a quest was captured with, and where - so within-class variance shows.
			var variantsByQuest = new Dictionary<uint, Dictionary<string, List<string>>>();
			var shapes = new Dictionary<(int, int, int, int), int>();
			var offers = new List<(string Capture, uint Quest, ConanClass Class, IReadOnlyList<uint> Options, uint Picked)>();
			var grants = new List<(string Capture, uint Quest, ConanClass Class, uint Template, bool WasOffered)>();
			var addWire = CapturedPacket.Wire(GameCharInterfaceMethods.DispatchAddItem);

			foreach (var capture in captures)
			{
				var cls = CaptureCharacterImporter.DetectPlayedClass(capture.Packets) ?? (ConanClass)0;
				var sets = new Dictionary<uint, RewardSets>();
				// The latest area list the server sent this session, per line - what MainQuest_c's vector holds after a
				// live journal update (assumed: the journal IIR is what refreshes it mid-session).
				var journalLists = new Dictionary<uint, List<uint>>();
				var lastOffer = new Dictionary<uint, (int At, IReadOnlyList<uint> Options)>();
				var picked = new Dictionary<uint, uint>();
				var completedAt = new List<(int At, uint Quest)>();

				for (var i = 0; i < capture.Packets.Count; i++)
				{
					var p = capture.Packets[i];
					if (p.IsIirCarrier && p.IsDownload && p.SubOpcode == FullPlayerCharacterCodec.Opcode)
					{
						try
						{
							var parts = FullPlayerCharacterCodec.Parse(p.Record);
							var blocks = parts.SubClass.QuestIndex?.Blocks ?? [];
							foreach (var b in blocks)
								lineCountVsCatalog.Add((b.AreaId, b.AreaQuestIds.Count, catalogSize.GetValueOrDefault(b.AreaId)));

							var records = parts.SubClass.Quests?.Records ?? [];
							foreach (var g in records.GroupBy(r => r.QuestLineId).Where(g => g.Count() > 1))
							{
								var aKeys = g.Select(r => $"{r.XpReward}/{r.MoneyReward}/{string.Join(",", r.Obj1.Select(e => e.TemplateId))}/{string.Join(",", r.Obj2.Select(e => e.TemplateId))}").Distinct().Count();
								var bKeys = g.Select(r => $"{r.FieldF0}/{r.FieldF8}/{string.Join(",", r.Obj3.Select(e => e.TemplateId))}/{string.Join(",", r.Obj4.Select(e => e.TemplateId))}").Distinct().Count();
								if (aKeys == 1) aSame++; else aDiff++;
								if (bKeys == 1) bSame++; else bDiff++;
							}

							foreach (var r in records)
							{
								Catalog(r, catalogRewards, ref bMatchesCatalog, ref bMismatch, ref bNoCatalog, ref aMatchesCatalog, ref aMismatch, ref aNoCatalog, mismatches, names);
								foreach (var e in r.Obj3.Concat(r.Obj4))
								{
									if (!itemLevel.TryGetValue(e.TemplateId, out var il)) levelUnknown++;
									else if (il == e.Level) levelAgree++;
									else levelDisagree++;
									var qk = (r.QuestLineId, e.TemplateId);
									(quantityByLineTemplate.TryGetValue(qk, out var qs) ? qs : quantityByLineTemplate[qk] = []).Add(e.Quantity);
									(quantityByTemplate.TryGetValue(e.TemplateId, out var qt) ? qt : quantityByTemplate[e.TemplateId] = []).Add(e.Quantity);
								}
								var sk = $"A xp {r.XpReward} money {r.MoneyReward} | B xp {r.FieldF0} money {r.FieldF8}";
								if (!slotVariants.TryGetValue(r.QuestId, out var sv))
									slotVariants[r.QuestId] = sv = [];
								sv[sk] = sv.GetValueOrDefault(sk) + 1;
								Remember(sets, r, shapes);
								Variant(variantsByQuest, r, $"{capture.Name}/{cls}/snapshot@{i}");
								var block = blocks.FirstOrDefault(b => b.AreaId == r.QuestLineId);
								if (block != null && block.AreaQuestIds.Count > 0)
								{
									var last = block.AreaQuestIds[^1] == r.QuestId;
									Score(clientPredicate, r, last);
									var set = SetOf(r);
									if ((set.StartsWith("A") && !last) || (set.StartsWith("B") && last))
										offDiagonal[(r.QuestId, set, block.AreaQuestIds[^1])] = offDiagonal.GetValueOrDefault((r.QuestId, set, block.AreaQuestIds[^1])) + 1;

									if (!perLine.TryGetValue(r.QuestLineId, out var lineRows))
										perLine[r.QuestLineId] = lineRows = [];
									lineRows.TryAdd(r.QuestId, (Slots(r, names), last));
								}
							}
						}
						catch
						{
							// a snapshot shape the codec cannot split - counted by --report coverage
						}
					}
					else if (p.IsIirCarrier && p.IsDownload && p.SubOpcode == QuestFullUpdateCodec.Opcode && p.TryParse(QuestFullUpdateCodec.Parse, out var full))
					{
						Remember(sets, full.Record, shapes);
						Variant(variantsByQuest, full.Record, $"{capture.Name}/{cls}/fullupdate@{i}");
						var fk = $"A xp {full.Record.XpReward} money {full.Record.MoneyReward} | B xp {full.Record.FieldF0} money {full.Record.FieldF8} (live)";
						if (!slotVariants.TryGetValue(full.Record.QuestId, out var fsv))
							slotVariants[full.Record.QuestId] = fsv = [];
						fsv[fk] = fsv.GetValueOrDefault(fk) + 1;
						if (journalLists.TryGetValue(full.Record.QuestLineId, out var sent) && sent.Count > 0)
							Score(clientPredicate, full.Record, sent[^1] == full.Record.QuestId);
					}
					else if (p.IsIirCarrier && p.IsDownload && p.SubOpcode == QuestJournalCodec.Opcode && p.TryParse(QuestJournalCodec.Parse, out var journal))
					{
						foreach (var area in new[] { journal.Area }.Concat(journal.MoreAreas))
							journalLists[area.QuestLineId] = area.Entries.Select(e => e.QuestId).ToList();
					}
					else if (p.IsIirCarrier && p.IsDownload && p.SubOpcode == QuestRewardNotifyCodec.Opcode && p.TryParse(QuestRewardNotifyCodec.Parse, out var notify))
					{
						if (notify.Options.Count > 0)
							lastOffer[notify.QuestId] = (i, notify.Options.Select(o => o.ItemTemplateId).ToList());
						else if (lastOffer.TryGetValue(notify.QuestId, out var offer) && notify.Trailing.Count > 3 && notify.Trailing[3] < offer.Options.Count)
						{
							picked[notify.QuestId] = offer.Options[(int)notify.Trailing[3]];
							completedAt.Add((i, notify.QuestId)); // the CONFIRM is a completion anchor too - the grant follows it
						}
					}
					else if (p.IsIirCarrier && p.IsDownload && p.SubOpcode == QuestCodec.Opcode && p.TryParse(QuestCodec.Parse, out var q) && q.State == QuestCodec.StateCompleted)
					{
						completedAt.Add((i, q.QuestId));
					}
					else if (p.IsDownload && p.WireOpcode == addWire)
					{
						AddItem add;
						try
						{
							add = AddItemCodec.Parse(p.Body);
						}
						catch
						{
							continue;
						}

						var template = add.Entry?.ItemData?.Template1 ?? 0;
						if (template == 0)
							continue;

						// The completion this grant belongs to: the most recent one inside the window.
						var owner = completedAt.LastOrDefault(c => i - c.At <= GrantWindowPackets && i >= c.At);
						if (owner.Quest == 0)
							continue;

						var offered = lastOffer.TryGetValue(owner.Quest, out var o) && o.Options.Contains(template);
						grants.Add((capture.Name, owner.Quest, cls, template, offered));
					}
				}

				foreach (var (quest, at) in lastOffer)
					offers.Add((capture.Name, quest, cls, at.Options, picked.GetValueOrDefault(quest)));

				foreach (var (quest, s) in sets)
				{
					byQuestClass.TryAdd((quest, cls), s);
					var a = s.Obj1.Count + s.Obj2.Count > 0;
					var b = s.Obj3.Count + s.Obj4.Count > 0;
					setOf[quest] = a && b ? "A+B" : a ? "A (obj1/obj2)" : b ? "B (obj3/obj4)" : "neither";
				}
			}

			Console.WriteLine("=== the four reward lists of Quest_c, against what retail handed out (#893) ===\n");

			// 1. Shape: how the four counts co-occur.
			Console.WriteLine("  list-count shapes (obj1,obj2,obj3,obj4) over every captured journal record:");
			foreach (var (shape, n) in shapes.OrderByDescending(kv => kv.Value).Take(14))
				Console.WriteLine($"    {shape,-16} x{n}");
			Console.WriteLine();

			// 1b. THE cross-tab: the client renders set A on a line's LAST step and set B otherwise, so if the server
			// populates the set the client will show, A-quests should be last steps and B-quests mid steps.
			Console.WriteLine("  which SET a quest populates, against whether it is its line's LAST step (QuestLines.LastQuestId):");
			Console.WriteLine($"    {"set",-16} {"last step",10} {"mid step",10}");
			foreach (var g in setOf.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
				Console.WriteLine($"    {g.Key,-16} {g.Count(kv => lastOfLine.Contains(kv.Key)),10} {g.Count(kv => !lastOfLine.Contains(kv.Key)),10}");
			// 1b''. The client's predicate, evaluated on the list the server actually sent.
			Console.WriteLine("  ...and against the CLIENT'S OWN test - 'last entry of the area list the server sent' (per record):");
			Console.WriteLine($"    {"set",-16} {"is last",10} {"not last",10}");
			foreach (var set in clientPredicate.Keys.Select(k => k.Set).Distinct().OrderBy(x => x))
				Console.WriteLine($"    {set,-16} {clientPredicate.GetValueOrDefault((set, true)),10} {clientPredicate.GetValueOrDefault((set, false)),10}");
			var whole = lineCountVsCatalog.Count(x => x.Sent == x.Catalog);
			var partial = lineCountVsCatalog.Count(x => x.Sent < x.Catalog);
			Console.WriteLine($"    (the sent area list == the line's whole catalog on {whole} of {lineCountVsCatalog.Count} index blocks; shorter on {partial})");
			Console.WriteLine();

			Console.WriteLine("  the OFF-DIAGONAL, by quest (set A on a non-last record / set B on a last one):");
			foreach (var ((quest, set, sentLast), n) in offDiagonal.OrderByDescending(kv => kv.Value))
				Console.WriteLine($"    quest {quest,-6} line {lineOf.GetValueOrDefault(quest),-5} {set,-14} sent list ends at {sentLast,-6} x{n}");
			Console.WriteLine();

			Console.WriteLine("  per LINE, every captured step side by side (does set B repeat down the line?):");
			foreach (var (line, rows) in perLine.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key))
			{
				Console.WriteLine($"    line {line}:");
				foreach (var (quest, (slots, last)) in rows.OrderBy(kv => kv.Key))
					Console.WriteLine($"      {quest,-6} {(last ? "LAST" : "    ")}  {slots}");
			}

			Console.WriteLine();

			Console.WriteLine($"  within ONE snapshot, a line held at 2+ steps: A slots agree down the line {aSame}x / differ {aDiff}x;  B slots agree {bSame}x / differ {bDiff}x");
			Console.WriteLine();
			Console.WriteLine("  slot VARIANTS per quest (the XP/money words are NOT constant per quest):");
			foreach (var quest in new uint[] { 10238, 10287, 10830, 10928, 13479, 12371 })
			{
				if (!slotVariants.TryGetValue(quest, out var sv))
					continue;

				Console.WriteLine($"    quest {quest}:");
				foreach (var (k, n) in sv.OrderByDescending(kv => kv.Value))
					Console.WriteLine($"      x{n,-4} {k}");
			}

			Console.WriteLine();

			Console.WriteLine("  set B (obj3/obj4) against the CLIENT CATALOG's line reward (MainQuest.reward / optional_reward), per record:");
			Console.WriteLine($"    B populated: {bMatchesCatalog} match the line's catalog lists exactly, {bMismatch} differ, {bNoCatalog} on a line the catalog gives no items");
			Console.WriteLine($"    A populated: {aMatchesCatalog} equal the line's catalog lists, {aMismatch} differ, {aNoCatalog} on a line with no catalog items");
			foreach (var m in mismatches.Distinct().Take(12))
				Console.WriteLine($"      {m}");
			Console.WriteLine($"    line-half element LEVEL == Items.Level: {levelAgree} agree, {levelDisagree} disagree, {levelUnknown} template has no stored level");
			Console.WriteLine($"    line-half element QUANTITY constant per TEMPLATE across lines: {quantityByTemplate.Count(kv => kv.Value.Count == 1)}/{quantityByTemplate.Count}"
			                  + (quantityByTemplate.Any(kv => kv.Value.Count > 1) ? " - varies for " + string.Join(", ", quantityByTemplate.Where(kv => kv.Value.Count > 1).Select(kv => $"{(names.TryGetValue(kv.Key, out var qn) ? qn : kv.Key.ToString())} {{{string.Join(",", kv.Value)}}}")) : ""));
			Console.WriteLine($"    line-half element QUANTITY constant per (line, template): {quantityByLineTemplate.Count(kv => kv.Value.Count == 1)}/{quantityByLineTemplate.Count}; values {string.Join(",", quantityByLineTemplate.SelectMany(kv => kv.Value).Distinct().OrderBy(x => x))}");
			Console.WriteLine();

			// 1b'. The RIVAL predicate. The client's MainQuest_c holds the line's quests in a vector and tests the LAST
			// element; if that vector is sorted by quest ID rather than by journal order, "last" is the line's HIGHEST
			// id - which is 10928 for its line, not 10836. Scored the same way so the two readings can be compared.
			var maxIdOfLine = lineOf.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Max(kv => kv.Key));
			bool IsMaxId(uint q) => lineOf.TryGetValue(q, out var l) && maxIdOfLine[l] == q;
			Console.WriteLine("  ...and against 'this quest has the HIGHEST id in its line' (the vector sorted by id):");
			Console.WriteLine($"    {"set",-16} {"max id",10} {"not max",10}");
			foreach (var g in setOf.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
				Console.WriteLine($"    {g.Key,-16} {g.Count(kv => IsMaxId(kv.Key)),10} {g.Count(kv => !IsMaxId(kv.Key)),10}");
			Console.WriteLine();

			// 1c. The exceptions, in full - a cross-tab that is 66/68 clean is decided by the two it is not.
			foreach (var (quest, set) in setOf.Where(kv => (kv.Value.StartsWith("A") && !lastOfLine.Contains(kv.Key)) || (kv.Value.StartsWith("B") && lastOfLine.Contains(kv.Key))))
			{
				var r = byQuestClass.First(kv => kv.Key.Quest == quest).Value;
				var line = lineOf.GetValueOrDefault(quest);
				Console.WriteLine($"    EXCEPTION quest {quest} (line {line}, {(lastOfLine.Contains(quest) ? "last" : "mid")} step) populates {set}:");
				Console.WriteLine($"      obj1 [{Names(r.Obj1, names)}]  obj2 [{Names(r.Obj2, names)}]  obj3 [{Names(r.Obj3, names)}]  obj4 [{Names(r.Obj4, names)}]");
			}

			Console.WriteLine();

			// 1d. Every variant of the exception quests, by capture - within-class variance is invisible to first-wins.
			foreach (var quest in new uint[] { 10928, 17403, 13467 })
			{
				if (!variantsByQuest.TryGetValue(quest, out var vs))
					continue;

				Console.WriteLine($"    variants of quest {quest}: {vs.Count}");
				foreach (var (key, where) in vs)
					Console.WriteLine($"      {key}\n        seen {where.Count}x, e.g. {string.Join(" | ", where.Take(3))}");
			}

			Console.WriteLine();

			// 2. Offers.
			Console.WriteLine($"  pick-one OFFERS ({offers.Count}): which list holds every offered option?");
			foreach (var (cap, quest, cls, options, pick) in offers)
			{
				var sets = byQuestClass.TryGetValue((quest, cls), out var s) ? s : byQuestClass.FirstOrDefault(kv => kv.Key.Quest == quest).Value;
				var where = sets == null ? "record not captured" : string.Join(" ", options.Select(o => sets.Membership(o)).Distinct());
				var last = lastOfLine.Contains(quest) ? "LAST step" : "mid step";
				Console.WriteLine($"    quest {quest,-6} {last,-9} {cls,-14} {options.Count} options -> {where}   picked {pick} {(pick != 0 && names.TryGetValue(pick, out var pn) ? pn : "")}   [{cap[..Math.Min(40, cap.Length)]}]");
			}

			Console.WriteLine();

			// 3. Grants.
			Console.WriteLine($"  completion GRANTS ({grants.Count}): which list holds the item DispatchAddItem delivered?");
			foreach (var (cap, quest, cls, template, offered) in grants)
			{
				var sets = byQuestClass.TryGetValue((quest, cls), out var s) ? s : byQuestClass.FirstOrDefault(kv => kv.Key.Quest == quest).Value;
				var where = sets == null ? "record not captured" : sets.Membership(template);
				var last = lastOfLine.Contains(quest) ? "LAST step" : "mid step";
				Console.WriteLine($"    quest {quest,-6} {last,-9} {cls,-14} {template,8} {(names.TryGetValue(template, out var n) ? n : "?"),-28} -> {where}{(offered ? "  (the picked option)" : "")}   [{cap[..Math.Min(40, cap.Length)]}]");
			}

			Console.WriteLine();

			// 4. Class variance.
			var multiClass = byQuestClass.GroupBy(kv => kv.Key.Quest).Where(g => g.Select(kv => kv.Key.Class).Distinct().Count() > 1).ToList();
			Console.WriteLine($"  quests captured under MORE THAN ONE class: {multiClass.Count}");
			var differing = 0;
			foreach (var g in multiClass)
			{
				var variants = g.Select(kv => (kv.Key.Class, Key: $"{string.Join(",", kv.Value.Obj1)}|{string.Join(",", kv.Value.Obj2)}|{string.Join(",", kv.Value.Obj3)}|{string.Join(",", kv.Value.Obj4)}")).ToList();
				if (variants.Select(v => v.Key).Distinct().Count() > 1)
				{
					differing++;
					Console.WriteLine($"    quest {g.Key}: the lists DIFFER by class");
					foreach (var (c, key) in variants)
						Console.WriteLine($"      {c,-14} {key}");
				}
			}

			Console.WriteLine($"  ...of which the four lists differ between classes: {differing}");
			if (differing == 0 && multiClass.Count > 0)
				Console.WriteLine("  -> no list is class-specific on the evidence available: a quest's lists are the same whoever holds it.");

			Console.WriteLine();
			Console.WriteLine("  Reading (from the client's journal renderer, FUN_01617260): on a line's LAST step the journal shows");
			Console.WriteLine("  obj1 as rewards and obj2 as the choose-one; on any other step obj3 and obj4. The offer and grant rows");
			Console.WriteLine("  above are the corpus's answer to whether retail's SERVER agrees with its own client.");
			return 0;
		}

		private static void Catalog(QuestcRecord r, Dictionary<uint, (List<uint> Guaranteed, List<uint> Optional)> catalog,
			ref int bMatch, ref int bMismatch, ref int bNone, ref int aMatch, ref int aMismatch, ref int aNone, List<string> mismatches, Dictionary<uint, string> names)
		{
			var has = catalog.TryGetValue(r.QuestLineId, out var cat);
			if (r.Obj3.Count + r.Obj4.Count > 0)
			{
				if (!has) bNone++;
				else if (cat.Guaranteed.SequenceEqual(r.Obj3.Select(e => e.TemplateId).OrderBy(x => x)) && cat.Optional.SequenceEqual(r.Obj4.Select(e => e.TemplateId).OrderBy(x => x))) bMatch++;
				else { bMismatch++; mismatches.Add($"B quest {r.QuestId} line {r.QuestLineId}: obj3 [{Names(r.Obj3.Select(e => e.TemplateId).ToList(), names)}] obj4 [{Names(r.Obj4.Select(e => e.TemplateId).ToList(), names)}] vs catalog [{Names(cat.Guaranteed, names)}] / [{Names(cat.Optional, names)}]"); }
			}

			if (r.Obj1.Count + r.Obj2.Count > 0)
			{
				if (!has) aNone++;
				else if (cat.Guaranteed.SequenceEqual(r.Obj1.Select(e => e.TemplateId).OrderBy(x => x)) && cat.Optional.SequenceEqual(r.Obj2.Select(e => e.TemplateId).OrderBy(x => x))) aMatch++;
				else { aMismatch++; mismatches.Add($"A quest {r.QuestId} line {r.QuestLineId}: obj1 [{Names(r.Obj1.Select(e => e.TemplateId).ToList(), names)}] obj2 [{Names(r.Obj2.Select(e => e.TemplateId).ToList(), names)}] vs catalog [{Names(cat.Guaranteed, names)}] / [{Names(cat.Optional, names)}]"); }
			}
		}

		private static string SetOf(QuestcRecord r)
		{
			var a = r.Obj1.Count + r.Obj2.Count > 0;
			var b = r.Obj3.Count + r.Obj4.Count > 0;
			return a && b ? "A+B" : a ? "A (obj1/obj2)" : b ? "B (obj3/obj4)" : "neither";
		}

		private static string Slots(QuestcRecord r, Dictionary<uint, string> names)
		{
			return $"A: xp {r.XpReward} money {r.MoneyReward} obj1 [{Names(r.Obj1.Select(e => e.TemplateId).ToList(), names)}] obj2 [{Names(r.Obj2.Select(e => e.TemplateId).ToList(), names)}]"
			       + $"  |  B: xp {r.FieldF0} money {r.FieldF8} obj3 [{Names(r.Obj3.Select(e => e.TemplateId).ToList(), names)}] obj4 [{Names(r.Obj4.Select(e => e.TemplateId).ToList(), names)}]";
		}

		private static void Score(Dictionary<(string, bool), int> into, QuestcRecord r, bool last)
		{
			var a = r.Obj1.Count + r.Obj2.Count > 0;
			var b = r.Obj3.Count + r.Obj4.Count > 0;
			var set = a && b ? "A+B" : a ? "A (obj1/obj2)" : b ? "B (obj3/obj4)" : "neither";
			into[(set, last)] = into.GetValueOrDefault((set, last)) + 1;
		}

		private static string Names(IReadOnlyList<uint> ids, Dictionary<uint, string> names)
		{
			return string.Join(", ", ids.Select(i => names.TryGetValue(i, out var n) ? $"{n} ({i})" : i.ToString()));
		}

		private static void Variant(Dictionary<uint, Dictionary<string, List<string>>> into, QuestcRecord r, string where)
		{
			var key = $"obj1[{string.Join(",", r.Obj1.Select(e => e.TemplateId))}] obj2[{string.Join(",", r.Obj2.Select(e => e.TemplateId))}] obj3[{string.Join(",", r.Obj3.Select(e => e.TemplateId))}] obj4[{string.Join(",", r.Obj4.Select(e => e.TemplateId))}]";
			if (!into.TryGetValue(r.QuestId, out var vs))
				into[r.QuestId] = vs = [];
			if (!vs.TryGetValue(key, out var list))
				vs[key] = list = [];
			list.Add(where);
		}

		private static void Remember(Dictionary<uint, RewardSets> sets, QuestcRecord r, Dictionary<(int, int, int, int), int> shapes)
		{
			var shape = (r.Obj1.Count, r.Obj2.Count, r.Obj3.Count, r.Obj4.Count);
			shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
			sets.TryAdd(r.QuestId, new RewardSets(r.QuestId,
				r.Obj1.Select(e => e.TemplateId).ToList(), r.Obj2.Select(e => e.TemplateId).ToList(),
				r.Obj3.Select(e => e.TemplateId).ToList(), r.Obj4.Select(e => e.TemplateId).ToList()));
		}
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Constants;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// One NPC name's spawn-diff outcome. A class rather than a tuple because the counters are bumped one at a
		// time down a long loop, and a record struct there reads as five re-assignments of the whole verdict.
		private sealed class SpawnVerdict
		{
			public int Same;
			public int Differ;
			public int NoRow;
			public int Failed;
			public string? Note;
		}

		// Does an NPC spawn carry any per-NPC STAT array? (#401's player->NPC half needs an armour source, and the
		// stat-update IIR carries none - tools/capture/scan_npc_armor_stat.py.) The spawn's character message has a
		// `generic_stats` repeated-uint32 field; this censuses it over every captured NPC spawn using the real
		// protobuf decoder, because hand-scanning the wire for the field tag matches inside strings.
		// --report spawn-diff [--report-arg <name substring>]
		//
		// Rebuilds every CAPTURED NPC spawn from the DB row it was imported into and compares the two - the question
		// "does the NPC we send look like the NPC retail sent", asked of the shipped data rather than of a golden
		// fixture. That is what an in-client "this NPC renders wrong" report needs, and what the equipment-row and
		// stat-core queries cannot answer on their own: both can be faithful while the record assembled from them is
		// not.
		//
		// It compares the two parts that DESCRIBE the entity and that we claim to reproduce - the embedded
		// CoreCharacter message (identity, stats, worn equipment) and the post-IIR appearance/morph table - plus the
		// CoreChar header word. It deliberately does NOT compare whole records: our pre-IIR is the generated minimal
		// scaffold by design (#243), so a byte-exact record comparison reports every spawn in the corpus as different
		// and says nothing about appearance.
		//
		// The match is exact: a captured record is found by the row's OWN derived key (map + position), over the login
		// burst only - the window the importer imports through, since a mid-session spawn is the same NPC at wherever
		// it had WANDERED to and keys to no row. A row we cannot find, or a type with no lifted stat core, is reported
		// rather than skipped: "not compared" is a different answer from "matches".
		//
		// ⚠️ NOROW is not by itself a data gap. The key is built with a NULL class, so every placement on a
		// CLASS-SPECIFIC map (the city armories, where the class is part of the identity) keys to nothing here - the
		// quartermasters are all NOROW for that reason alone. Read a NOROW as "go look", not as "missing".
		private static void SpawnDiff(IReadOnlyList<NamedCapture> captures, string? filter)
		{
			// The client's spawn poll, which ends the login-burst window (NpcSpawnExtractor names it the same way).

			using var db = OpenDb();
			var rows = db
				.NpcSpawns.AsNoTracking()
				.Include(s => s.NpcTemplate).ThenInclude(t => t.StatCore)
				.Include(s => s.Equipment).ThenInclude(e => e.Item)
				.Include(s => s.Morphs)
				.AsSplitQuery()
				.ToDictionary(s => s.Id);

			// Per template: how the captured records for it compared. Keyed by NAME because that is what an in-client
			// report names, and a name can span several template ids (two "Poacher" types stand on one map).
			var tally = new SortedDictionary<string, SpawnVerdict>();
			var seen = new HashSet<(uint, float, float, float)>();
			var word2Tables = 0;
			var word2NonZero = 0;
			var word2Values = new Dictionary<uint, int>();
			var word2Prefix = new Dictionary<string, int>();
			var word2Carriers = new HashSet<(uint Map, float X, float Y, float Z)>();

			foreach (var capture in captures)
			{
				uint map = 0;
				var sawPoll = false;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					// The LOGIN BURST window only, the same gate NpcSpawnExtractor.ExtractLoginBurst imports through
					// (the client's spawn poll ends it).
					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (sawPoll || map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue; // a body the codec cannot account for is not one we build; #243's mid-session forms
					}

					// Neither the player's own spawn nor another player's is an NpcSpawn row.
					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
						continue;

					// ⚠️ The Word2 census runs HERE - before the name filter, the position dedup and the DB-row
					// lookup - because it is a claim about retail's BYTES, not about the spawns we can compare. It
					// used to sit at the bottom of the loop, past all three gates, and reported "2 of 756 tables"
					// while the importer's own lift found a THIRD carrying spawn (Valkyrie Skalla) that the dedup had
					// dropped. A census that silently answers a narrower question than it states is the failure mode
					// this whole field's history is made of.
					if (captured.Post?.Slot is { Appearance.Count: > 0, Skills.Count: 0, Pets.Count: 0 } census)
					{
						word2Tables++;
						var nonZero = census.Appearance.FindAll(m => m.Word2 != 0);
						if (nonZero.Count > 0)
						{
							word2NonZero++;
							word2Carriers.Add((map, captured.Position.X, captured.Position.Y, captured.Position.Z));
							foreach (var m in nonZero)
								word2Values[m.Word2] = word2Values.GetValueOrDefault(m.Word2) + 1;

							// "the first N entries and no others" is the shape an ordered appearance field has and
							// scattered runtime state does not.
							var prefix = census.Appearance.FindIndex(m => m.Word2 == 0);
							var contiguous = prefix >= 0 && !census.Appearance.Skip(prefix).Any(m => m.Word2 != 0);
							var key = contiguous ? $"{prefix} entries" : "scattered";
							word2Prefix[key] = word2Prefix.GetValueOrDefault(key) + 1;
						}
					}

					var name = captured.Character?.Name ?? "";
					if (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
						continue;

					// One verdict per PHYSICAL spawn, not per captured send: the same NPC is re-sent on every login in
					// every capture, and counting those would weight the tally by how often a zone was visited.
					var pos = captured.Position;
					if (!seen.Add((map, pos.X, pos.Y, pos.Z)))
						continue;

					var v = tally.GetValueOrDefault(name) ?? (tally[name] = new SpawnVerdict());
					if (!rows.TryGetValue(NpcSpawn.ComputeId(map, pos.X, pos.Y, pos.Z, null), out var row))
					{
						v.NoRow++;
						continue;
					}

					SimpleCharFullUpdateIIR ours;
					try
					{
						ours = SimpleCharFullUpdateCodec.Parse(NpcSpawnBuilder.BuildLive(row, captured.Handle, (uint)captured.Playfield));
					}
					catch (Exception e) when (e is InvalidOperationException or CodecParseException)
					{
						v.Failed++;
						v.Note ??= e.Message;
						continue;
					}

					var faults = new List<string>();
					if (captured.CoreCharIirHeader != ours.CoreCharIirHeader)
						faults.Add($"iir header 0x{captured.CoreCharIirHeader:X} vs 0x{ours.CoreCharIirHeader:X}");

					var theirMsg = captured.Character?.ToProtobuf() ?? [];
					var ourMsg = ours.Character?.ToProtobuf() ?? [];
					if (!theirMsg.AsSpan().SequenceEqual(ourMsg))
					{
						// WHICH fields, not just "the message differs". Named fields are what turn a diff into a lead:
						// "we send MonsterScale 100 where retail sends 97" is actionable, "758B vs 758B" is not.
						var fields = FieldDiff(captured.Character, ours.Character);
						faults.Add($"message {theirMsg.Length}B vs {ourMsg.Length}B"
							+ $" (equip {captured.Character?.InventoryEntries.Count ?? 0} vs {ours.Character?.InventoryEntries.Count ?? 0})"
							+ (fields.Count > 0 ? $" [{string.Join(", ", fields.Take(6))}{(fields.Count > 6 ? ", ..." : "")}]" : ""));
					}

					var theirMorphs = Morphs(captured);
					var ourMorphs = Morphs(ours);
					if (!theirMorphs.SequenceEqual(ourMorphs))
						faults.Add($"appearance {theirMorphs.Count} morphs vs {ourMorphs.Count}"
							+ (theirMorphs.Count == ourMorphs.Count
								? " (same count, different values)"
								: theirMorphs.Count > 0 && ourMorphs.Count == 0
									? $" - BALD, declined because {AppearanceDeclineReason(captured)}"
									: ""));
					else if (captured.Post?.MovementMode != ours.Post?.MovementMode)
						faults.Add($"post-IIR movement mode {captured.Post?.MovementMode} vs {ours.Post?.MovementMode}");

					if (faults.Count == 0)
					{
						v.Same++;
						continue;
					}

					v.Differ++;
					v.Note ??= string.Join("; ", faults);
				}
			}

			// #672: the classifier declines a whole appearance table on ONE entry with a non-zero Word2, and every
			// bald NPC found so far declines for exactly that. So census the field across every captured table: if
			// it were runtime noise it would be scattered, and if it is part of the appearance it will have shape.
			Console.WriteLine();
			Console.WriteLine("appearance Word2 census (the field that USED to decline a table - #672):");
			Console.WriteLine($"  captured sends with a morph list: {word2Tables}, of which {word2NonZero} carry a non-zero Word2 on some entry"
			                  + $" ({word2Carriers.Count} distinct placement(s))");
			Console.WriteLine($"  Word2 values: {string.Join(", ", word2Values.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} x{kv.Value}"))}");
			Console.WriteLine($"  and on a carrying table, the non-zero entries are its FIRST {string.Join("/", word2Prefix.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ({kv.Value})"))}");

			// ⚠️ Cross-check against what the IMPORTER lifted, because this report can only see the LOGIN BURST and
			// the importer sees more. That gap is not hypothetical: the burst window shows two carrying placements
			// (Turach, Kincaid) and the DB holds three - `Valkyrie Skalla` carries one from a send this window never
			// reaches. Reporting only the burst figure would state a floor as if it were the count, which is exactly
			// the error #672 was opened about.
			var carriedInDb = rows.Values.Where(r => r.Morphs.Any(m => m.Word2 != 0)).Select(r => r.NpcTemplate?.Name ?? $"#{r.NpcTemplateId}").Order().ToList();
			Console.WriteLine($"  the DB (which the importer lifts from a WIDER window than this report sees) holds "
			                  + $"{carriedInDb.Count} carrying placement(s): {string.Join(", ", carriedInDb)}");

			// ⭐ WHAT it is attached to. The ordinal shape alone says "not runtime state"; this says what it IS.
			// Resolving each carrying morph id against MorphTargets: every one is a `*texscript_*`, never a face/body
			// slider or a hair mesh. And no single-texscript table carries it, which is what an ORDER needs two of -
			// though most multi-texscript tables do not carry it either, so it is not simply "the layer order".
			var morphNames = db.MorphTargets.AsNoTracking().ToDictionary(t => t.Id, t => t.Name);
			var carryingKinds = rows.Values
				.SelectMany(r => r.Morphs.Where(m => m.Word2 != 0))
				.GroupBy(m => morphNames.GetValueOrDefault(m.MorphId, "?").StartsWith('*') ? morphNames[m.MorphId].Split('_')[0] : "a plain morph slider")
				.ToDictionary(g => g.Key, g => g.Count());
			var texscriptRuns = rows.Values
				.Select(r => r.Morphs.Count(m => morphNames.GetValueOrDefault(m.MorphId, "").StartsWith("*texscript", StringComparison.Ordinal)))
				.Where(n => n > 0)
				.GroupBy(n => n)
				.ToDictionary(g => g.Key, g => g.Count());
			Console.WriteLine($"  and the carrying ENTRIES are: {string.Join(", ", carryingKinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"))}");
			Console.WriteLine($"  tables by texscript count: {string.Join(", ", texscriptRuns.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}->{kv.Value}"))}"
			                  + $" (single-texscript tables carrying the ordinal: {rows.Values.Count(r => r.Morphs.Count(m => morphNames.GetValueOrDefault(m.MorphId, "").StartsWith("*texscript", StringComparison.Ordinal)) == 1 && r.Morphs.Any(m => m.Word2 != 0))})");
			Console.WriteLine();

			Console.WriteLine("spawn-diff: every captured NPC spawn rebuilt from its DB row - CoreCharacter message +");
			Console.WriteLine("appearance table compared (the pre-IIR is our generated scaffold by design, so it is not).");
			Console.WriteLine("DIFFER = an NPC we describe differently from retail. NOROW/FAILED were not compared at all.");
			Console.WriteLine();
			Console.WriteLine($"{"npc",-34} {"same",6} {"differ",7} {"norow",6} {"failed",7}");
			foreach (var (name, v) in tally.OrderByDescending(x => x.Value.Differ + x.Value.Failed).ThenBy(x => x.Key))
			{
				Console.WriteLine($"{(name.Length > 33 ? name[..33] : name),-34} {v.Same,6} {v.Differ,7} {v.NoRow,6} {v.Failed,7}");
				if (v.Note != null)
					Console.WriteLine($"    {v.Note}");
			}

			Console.WriteLine();
			Console.WriteLine($"totals: {tally.Sum(t => t.Value.Same)} match, {tally.Sum(t => t.Value.Differ)} differ, "
				+ $"{tally.Sum(t => t.Value.NoRow)} with no DB row, {tally.Sum(t => t.Value.Failed)} unbuildable, over {tally.Count} names");

			return;

			// The appearance table as (morph id, value) pairs. Empty for an animal, whose post-IIR carries no entry
			// slot of this shape - so "0 vs 0" is a match, not two absences papering over each other.
			// #672: WHY a captured appearance table did not become morph rows. `NpcSpawnAppearance.TryLiftLenient` is
			// deliberately narrower than the reader it runs on and declines the whole table on any of four
			// conditions; a spawn that lands here renders BALD, so which condition fired is the whole question. It
			// re-states the classifier's conditions rather than calling it, because the classifier answers bool - and
			// the two are gated together by NpcSpawnAppearanceTests, so they cannot drift apart silently.
			static string AppearanceDeclineReason(SimpleCharFullUpdateIIR captured)
			{
				var post = captured.Post;
				if (post == null)
					return "the post-IIR did not decode";
				if (post.Slot is null)
					return "the entry slot is absent";
				if (post.Slot.Appearance.Count == 0)
					return "the table is empty (the minimal no-morph form)";

				// Word2 came OFF this veto with #672 - it is an ordinal, not runtime state, and the column now
				// carries it. (The old never-observed-Flag veto died with the field: it was the u16 string length's
				// high byte, #466.) A non-empty skill or pet list marks a PLAYER stream.
				if (post.Slot.Skills.Count > 0 || post.Slot.Pets.Count > 0)
					return $"the slot carries skills/pets ({post.Slot.Skills.Count}/{post.Slot.Pets.Count}) - a player stream";
				if (post.EndPairs.Count > 0 || post.EndPairs2.Count > 0 || post.EndPairs3.Count > 0)
					return $"a closing list is not empty ({post.EndPairs.Count}/{post.EndPairs2.Count}/{post.EndPairs3.Count} pairs)";

				return "no classifier condition fires - the rows are missing for another reason (a stale import?)";
			}

			static List<(uint, float)> Morphs(SimpleCharFullUpdateIIR spawn)
			{
				return spawn.Post?.Slot is { Skills.Count: 0, Pets.Count: 0 } slot
					? slot.Appearance.Select(m => (m.MorphId, m.Value)).ToList()
					: [];
			}
		}

		// Names the SCALAR fields two protobuf messages disagree on, recursing into sub-messages. Reflective rather
		// than a hand-written field list because the message is generated: a field added to the schema has to show up
		// here without anyone remembering to add it, or the diff quietly under-reports.
		//
		// Repeated fields report only their COUNT - element-wise diffing them is the caller's job when it matters
		// (worn equipment already has its own line) - and a null-vs-set sub-message is named rather than walked.
		// --report field-stability [--report-arg <name substring>]
		//
		// For every field of the captured NPC CoreCharacter message: is its value a property of the NPC TYPE, or of
		// the individual placement? Asked of retail's own bytes, with no DB involved.
		//
		// This is the question `--report spawn-diff` raises but cannot answer (Gitea #602). A spawn-diff difference
		// says our value disagrees with retail's; it does not say whether the disagreement is a bad lift or a field
		// that has no type-level value to lift. `NpcStatCoreColumns.Lift` is faithful to the message it is handed -
		// the fault, where there is one, is that a per-PLACEMENT field was lifted to the TYPE at all, and then every
		// placement of that type inherits whichever value the representative spawn happened to carry.
		//
		// The test is a GROUP BY, not a threshold: group the captured spawns by TYPE, keep the types sighted more
		// than once, and count in how many of them the field holds one distinct value. A type-stable field is
		// constant in ~all of them; a per-placement field is constant only where the type happens to have been
		// sighted in one state. Reported as that ratio plus the worst offender, so "83% stable" is readable as
		// "nearly type-stable, with a real exception to look at" rather than as a pass mark.
		//
		// ⚠️ The group key is retail's own `npc_stats.TemplateId`, NOT the name. Grouping by name was the first cut
		// and it is wrong: a name spans several templates (the same report scored `TemplateId` itself at 70% stable
		// under a name key - "Halen van Dummy" alone covers 22 of them), so every field inherits that conflation and
		// reads as more placement-varying than it is. A field cannot be less stable than the key it is grouped by.
		//
		// ⚠️ Only types sighted MORE THAN ONCE can say anything: a type seen once is trivially "constant" and would
		// inflate every field toward 100%. The single-sighting types are counted and reported separately.
		private static void FieldStability(IReadOnlyList<NamedCapture> captures, string? filter)
		{

			// field name -> npc TEMPLATE -> the distinct values seen for it
			var fields = new SortedDictionary<string, Dictionary<uint, HashSet<string>>>(StringComparer.Ordinal);
			var sightings = new Dictionary<uint, int>();
			var templateNames = new Dictionary<uint, string>();

			// ...and the same thing keyed by PHYSICAL PLACEMENT (map + position), which is the second half of the
			// question and the one that decides whether a per-placement field can live in a table AT ALL. A field
			// that varies between sightings of one placement is not placement-scoped either - it is rolled per spawn
			// EVENT, and no column can hold it. This accumulator deliberately does NOT dedup by position: repeated
			// sightings of one placement are exactly its evidence.
			var byPlacement = new SortedDictionary<string, Dictionary<(uint, float, float, float), HashSet<string>>>(StringComparer.Ordinal);
			var placementSightings = new Dictionary<(uint, float, float, float), int>();
			var placementNames = new Dictionary<(uint, float, float, float), string>();
			var seen = new HashSet<(uint, float, float, float)>();

			foreach (var capture in captures)
			{
				var sawPoll = false;
				uint map = 0;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (sawPoll || map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue;
					}

					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
						continue;

					var name = captured.Character?.Name ?? "";
					if (name.Length == 0 || (!string.IsNullOrEmpty(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
						continue;

					// The TYPE, from retail's own message - see the group-key warning above.
					var template = captured.Character?.npc_stats?.TemplateId ?? 0;
					if (template == 0)
						continue;

					var pos = captured.Position;
					var placement = (map, pos.X, pos.Y, pos.Z);
					var values = FieldValues(captured.Character);

					placementSightings[placement] = placementSightings.GetValueOrDefault(placement) + 1;
					placementNames[placement] = name;
					foreach (var (field, value) in values)
					{
						var perPlacement = byPlacement.TryGetValue(field, out var pd) ? pd : byPlacement[field] = new Dictionary<(uint, float, float, float), HashSet<string>>();
						(perPlacement.TryGetValue(placement, out var pv) ? pv : perPlacement[placement] = []).Add(value);
					}

					// One reading per PHYSICAL spawn for the TYPE table, the same dedup spawn-diff uses: the same NPC
					// is re-sent on every login in every capture, and counting those would score a field by how often
					// a zone was visited.
					if (!seen.Add(placement))
						continue;

					sightings[template] = sightings.GetValueOrDefault(template) + 1;
					templateNames[template] = name;
					foreach (var (field, value) in values)
					{
						var perType = fields.TryGetValue(field, out var d) ? d : fields[field] = new Dictionary<uint, HashSet<string>>();
						(perType.TryGetValue(template, out var vals) ? vals : perType[template] = []).Add(value);
					}
				}
			}

			var repeated = sightings.Where(s => s.Value > 1).Select(s => s.Key).ToHashSet();
			Console.WriteLine("field-stability: is a captured NPC field a property of the TYPE or of the PLACEMENT?");
			Console.WriteLine("Measured over retail's own spawns - no DB row is read, so this says nothing about our lift,");
			Console.WriteLine("only about whether there is a type-level value TO lift (Gitea #602).");
			Console.WriteLine();
			Console.WriteLine($"{sightings.Count} templates, {repeated.Count} of them sighted more than once "
				+ $"({sightings.Count - repeated.Count} single-sighting templates excluded - they are trivially constant).");
			Console.WriteLine();
			Console.WriteLine($"{"field",-40} {"stable",7} {"of",5}  {"%",6}  worst offender");

			var scored = new List<(string Field, int Stable, int Total, double Pct, string Worst)>();
			foreach (var (field, perName) in fields)
			{
				var considered = perName.Where(k => repeated.Contains(k.Key)).ToList();
				if (considered.Count == 0)
					continue;

				var stable = considered.Count(k => k.Value.Count == 1);
				var worst = considered.OrderByDescending(k => k.Value.Count).First();
				scored.Add((field, stable, considered.Count, 100.0 * stable / considered.Count,
					worst.Value.Count == 1 ? "" : $"{templateNames[worst.Key]} #{worst.Key} ({worst.Value.Count} values: {string.Join("/", worst.Value.Take(4))})"));
			}

			foreach (var s in scored.OrderBy(s => s.Pct).ThenBy(s => s.Field))
				Console.WriteLine($"{(s.Field.Length > 39 ? s.Field[..39] : s.Field),-40} {s.Stable,7} {s.Total,5}  {s.Pct,5:F1}%  {s.Worst}");

			// Second table: the SAME physical NPC, sighted on more than one login. A field unstable here cannot be
			// stored at all - not on the type, not on the placement.
			var repeatedPlacements = placementSightings.Where(k => k.Value > 1).Select(k => k.Key).ToHashSet();
			Console.WriteLine();
			Console.WriteLine("--- by PHYSICAL PLACEMENT (map + position), the same NPC seen on more than one login ---");
			Console.WriteLine("A field unstable HERE is rolled per spawn event and belongs in no table, type or placement.");
			Console.WriteLine();
			Console.WriteLine($"{repeatedPlacements.Count} placements sighted more than once, of {placementSightings.Count}.");
			Console.WriteLine();
			Console.WriteLine($"{"field",-40} {"stable",7} {"of",5}  {"%",6}  worst offender");

			var placementScored = new List<(string Field, int Stable, int Total, double Pct, string Worst)>();
			foreach (var (field, perPlacement) in byPlacement)
			{
				var considered = perPlacement.Where(k => repeatedPlacements.Contains(k.Key)).ToList();
				if (considered.Count == 0)
					continue;

				var stable = considered.Count(k => k.Value.Count == 1);
				var worst = considered.OrderByDescending(k => k.Value.Count).First();
				placementScored.Add((field, stable, considered.Count, 100.0 * stable / considered.Count,
					worst.Value.Count == 1 ? "" : $"{placementNames[worst.Key]} ({worst.Value.Count} values: {string.Join("/", worst.Value.Take(4))})"));
			}

			foreach (var s in placementScored.OrderBy(s => s.Pct).ThenBy(s => s.Field).Take(25))
				Console.WriteLine($"{(s.Field.Length > 39 ? s.Field[..39] : s.Field),-40} {s.Stable,7} {s.Total,5}  {s.Pct,5:F1}%  {s.Worst}");

			// Third table: the VALUE DISTRIBUTION of the fields the two tables above condemn. Knowing a field is
			// rolled per spawn event says a column cannot hold it; it does not say what WE should send instead. For a
			// clock the expected answer is 0 - but that is a measurement, not an assumption (Gitea #602 item 2), and
			// this is the measurement: if the field is an animation PHASE, retail's own values are spread across the
			// whole range and 0 is simply one of them, so sending 0 is inside retail's distribution rather than a
			// value retail never sends.
			var placementPct = placementScored.ToDictionary(s => s.Field, s => s.Pct);
			// THREE conditions, and dropping any one of them fills the table with fields that belong in neither of the
			// two above. `placement <= type + 2pp` is the per-spawn-event test proper; `type < 100` requires the field
			// to BE unstable before asking at which scope; and `placement < 100` is what separates a field that varies
			// WITHIN one placement from one that is perfectly placement-stable and merely varies between placements
			// (`PetOwnerInstance` is 98.2% / 100.0% - the 2pp band admits it, and it is placement-scoped, not rolled).
			var perEvent = scored
				.Where(s => s.Pct < 100.0
					&& placementPct.TryGetValue(s.Field, out var p)
					&& p < 100.0
					&& p <= s.Pct + 2.0)
				.OrderBy(s => s.Pct)
				.ToList();

			Console.WriteLine();
			Console.WriteLine("--- value distribution of the per-SPAWN-EVENT fields ---");
			Console.WriteLine("A field no more stable at PLACEMENT scope than at TYPE scope (within 2pp) is rolled per spawn");
			Console.WriteLine("event. What follows is what retail actually sends for each, so the value WE send can be chosen");
			Console.WriteLine("from inside retail's own range rather than guessed.");
			foreach (var s in perEvent)
			{
				var values = fields[s.Field].SelectMany(v => v.Value).ToList();
				var distinct = values.Distinct(StringComparer.Ordinal).ToList();
				var top = values.GroupBy(v => v, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Take(6).ToList();
				Console.WriteLine();
				Console.WriteLine($"{s.Field} - type {s.Pct:F1}% / placement {placementPct[s.Field]:F1}%, {distinct.Count} distinct values");
				Console.WriteLine($"  most common: {string.Join(", ", top.Select(g => $"{g.Key} (x{g.Count()})"))}");

				// Continuous or enumerated? A clock reads as hundreds of distinct floats with no value dominating; an
				// enum reads as a handful. That difference is what decides whether 0 is "the start of a cycle" or a
				// state the client would have to interpret.
				// ⚠️ TWO traps here, and the second one cost a wrong conclusion once already.
				//
				// (1) "parses as a float" is NOT the clock test - every integer id parses as a float, which reads
				//     `npc_stats.TemplateId` (560 distinct ids) as a continuous phase.
				// (2) Counting DISTINCT values is not the clock test either. `AnimIdleTimer` has 94 distinct values
				//     of which 92 lie strictly between 0 and 1, which reads as an obviously-continuous phase - but
				//     those 92 are seen 1-13 times each while the two ENDPOINTS are seen 264 and 269 times. Weighted
				//     by how often retail actually sends them, the field is ~90% bimodal 0/1 with a rare transient,
				//     not a uniformly-sampled clock. A real clock would be roughly uniform across the range.
				//
				// So the test is the FREQUENCY share of the strictly-interior values, never their distinct count.
				var interior = values.Count(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1);
				var interiorDistinct = distinct.Count(v => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and < 1);
				if (interiorDistinct > distinct.Count / 2)
				{
					var share = 100.0 * interior / values.Count;
					Console.WriteLine($"  {interiorDistinct} of {distinct.Count} distinct values lie strictly between 0 and 1, but they carry only {share:F0}% of");
					Console.WriteLine($"  sightings: 0 is sent {values.Count(v => v == "0")}x and 1 {values.Count(v => v == "1")}x, of {values.Count}.");
					Console.WriteLine(share > 50
						? "  CLOCK: the interior dominates, so this is a uniformly-sampled phase and any value is as good as another."
						: "  BIMODAL + TRANSIENT: the field RESTS at 0 or 1 and is only caught in between mid-blend. Which endpoint"
						+ "\n  it rests at is real information - do NOT flatten it to one value.");
				}
				else
				{
					var modal = top[0];
					Console.WriteLine($"  ENUMERATED: {distinct.Count} values, modal `{modal.Key}` on {100.0 * modal.Count() / values.Count:F0}% of spawns"
						+ " - the resting value, and what we should send absent a reason not to.");
				}
			}
		}

		// Flattens a protobuf message to name -> printed value, one entry per scalar leaf. Collections are
		// reported by COUNT: their contents are a separate question (equipment has its own lifted table) and a
		// per-element key would drown the per-field one this report exists to answer.
		//
		// ⚠️ PRESENCE IS A VALUE, and it has to be read through `ShouldSerializeX()` rather than off the property.
		// protobuf-net generates `get => __pbn__X.GetValueOrDefault()`, so an UNSET optional field reads back as 0
		// and is indistinguishable from one retail actually sent as 0. Both reports were scoring it that way, which
		// silently merged "the type does not carry this field" into "the type sends zero" - for
		// `npc_stats.spawn_animation` that is 904 of 1018 types folded into the modal bucket, i.e. most of what
		// looked like a 92% agreement. Presence is load-bearing everywhere else in this codebase (`Lift` copies a
		// field only when it is set, which is what keeps a rebuilt body byte-identical), so it is load-bearing here.
		private static List<(string Field, string Value)> FieldValues(object? message, string prefix = "", int depth = 0)
		{
			var values = new List<(string, string)>();
			if (message == null || depth > 3)
				return values;

			foreach (var prop in message.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
			{
				if (prop.GetIndexParameters().Length > 0)
					continue;

				object? v;
				try
				{
					v = prop.GetValue(message);
				}
				catch (System.Reflection.TargetInvocationException)
				{
					continue; // protobuf-net throws on an unset field with no default
				}

				var name = prefix + prop.Name;
				switch (v)
				{
					case System.Collections.ICollection c:
					{
						values.Add((name + " count", c.Count.ToString()));
						continue;
					}

					case null:
					{
						values.Add((name, "(absent)"));
						continue;
					}
				}

				if (message.GetType().GetMethod("ShouldSerialize" + prop.Name, Type.EmptyTypes) is { } present
					&& present.Invoke(message, null) is false)
				{
					values.Add((name, "(unset)"));
					continue;
				}

				if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string) || prop.PropertyType.IsEnum)
					values.Add((name, Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""));
				else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(byte[]))
					values.AddRange(FieldValues(v, name + ".", depth + 1));
			}

			return values;
		}

		// --report field-predictors [--report-arg <field name>]   (default: MovementMode)
		//
		// `field-stability` says WHERE a captured NPC field lives - type, placement, or neither. This asks the question
		// that comes next, and only for the fields it condemns: a field that is rolled per spawn event belongs in no
		// column, but it still has to be SENT, so is it COMPUTED from something else the same spawn already carries?
		// (Gitea #605.) If it is, the answer is not "store it" or "send the mode" but "derive it", and no column should
		// exist at all.
		//
		// The measure is PURITY: group every sighting by the candidate predictor's value and ask, within each group,
		// what share of sightings agree on the target's value. 100% means the predictor DETERMINES the target over the
		// corpus. The baseline to beat is printed first - it is the target's own modal share, i.e. what a constant
		// scores - because a predictor that merely reaches the baseline has explained nothing.
		//
		// ⚠️ THE TRAP, and it is the whole reason cardinality is in the table: purity rises for free with the number of
		// distinct predictor values. A predictor as fine-grained as the sample itself (a handle, a position, a float
		// timer) partitions every sighting into its own group and scores 100% while explaining nothing at all. So a
		// hit is only interesting when it is BOTH pure and coarse, and the report sorts by purity but prints the
		// distinct-value count next to it and marks anything above a twentieth of the sample count as `(fine)`.
		//
		// ⚠️ Candidates are NOT only the character message's own fields. The spawn body also carries the entity's
		// MOVE TASK (SpawnPostIir.MoveTask, #477), which is the one part of a spawn that describes what the NPC is
		// DOING rather than what it is - so the move plan's mode, its path flags and its waypoint count are added as
		// synthetic predictors. For a movement/gait field those are the hypothesis worth testing first, and they are
		// invisible to a report that only walks the protobuf.
		private static void FieldPredictors(IReadOnlyList<NamedCapture> captures, string? target)
		{
			target = string.IsNullOrWhiteSpace(target) ? "MovementMode" : target.Trim();

			// One row per SIGHTING - deliberately not deduped by placement, unlike the type table in field-stability.
			// The fields this report is for are rolled per spawn EVENT, so every event is a separate observation of
			// the thing being explained; collapsing them would throw away the variation being measured.
			var rows = new List<(string Target, Dictionary<string, string> Predictors)>();
			var names = new List<(string Target, string Name)>();
			var captureOf = new List<string>();
			var players = new List<(string MovementMode, string Target)>();
			// (phase, target value) -> sightings. See the census note in the loop.
			var byPhase = new Dictionary<(string Phase, string Value), int>();
			var skipped = 0;

			foreach (var capture in captures)
			{
				var sawPoll = false;
				uint map = 0;
				foreach (var p in capture.Packets)
				{
					if (p is { IsIirCarrier: true, IsDownload: true, SubOpcode: PlayfieldFCodec.Opcode })
					{
						if (p.TryParse(PlayfieldFCodec.Parse, out var change))
							map = change.MapId;

						sawPoll = false;
						continue;
					}

					if (!p.IsDownload && p.WireOpcode == NpcSpawnExtractor.QuestMarkerQueryWireOpcode)
					{
						sawPoll = true;
						continue;
					}

					if (map == 0 || p is not { IsIirCarrier: true, IsDownload: true, SubOpcode: SimpleCharFullUpdateCodec.Opcode })
						continue;

					SimpleCharFullUpdateIIR captured;
					try
					{
						captured = SimpleCharFullUpdateCodec.Parse(p.Record);
					}
					catch (CodecParseException)
					{
						continue;
					}

					// ⭐ The SPAWN PHASE census, and it runs on BOTH phases where the measurement below runs only on
					// the login burst (#607). That restriction is right for "is this field computed from something
					// else the spawn carries" - the burst is the set the importer lifts through - but it makes the
					// most obvious hypothesis about a field like `npc_stats.SpawnAnimation` UNTESTABLE by
					// construction: "play the spawn-in animation" is a property of the spawn EVENT, i.e. of whether
					// the NPC is APPEARING or was already standing there, and every row here is the second case.
					if (captured is { Handle: < EntityHandles.CharacterInstanceBase, Character.player_stats: null })
					{
						var pv = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
						var phase = sawPoll ? "mid-session" : "login burst";
						byPhase[(phase, pv.GetValueOrDefault(target, "(absent)"))] = byPhase.GetValueOrDefault((phase, pv.GetValueOrDefault(target, "(absent)"))) + 1;
					}

					if (sawPoll)
						continue;

					if (captured.Handle >= EntityHandles.CharacterInstanceBase || captured.Character?.player_stats != null)
					{
						// PLAYERS are excluded from the measurement - their scaffold is a different grammar and they
						// would swamp the NPC sample - but they are the independent SECOND family, so a candidate
						// found on NPCs is worth re-checking against them. Kept as a bare (candidate, target) tally.
						var pv = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
						if (pv.TryGetValue(target, out var pt) && pt != "(absent)")
							players.Add(($"{captured.Post.MovementMode}", pt));
						continue;
					}

					var values = FieldValues(captured.Character).ToDictionary(v => v.Field, v => v.Value, StringComparer.Ordinal);
					foreach (var (name, value) in MoveTaskPredictors(captured.Post))
						values[name] = value;

					if (!values.TryGetValue(target, out var targetValue) || targetValue == "(absent)")
					{
						skipped++;
						continue;
					}

					values.Remove(target);
					rows.Add((targetValue, values));
					names.Add((targetValue, captured.Character?.Name ?? ""));
					captureOf.Add(capture.Name);
				}
			}

			// Printed FIRST, because when it separates it answers the question the purity table cannot even ask.
			if (byPhase.Count > 0)
			{
				Console.WriteLine($"`{target}` by SPAWN PHASE - is it a property of the spawn EVENT rather than of the entity? (#607)");
				foreach (var phase in new[] { "login burst", "mid-session" })
				{
					var rowsIn = byPhase.Where(kv => kv.Key.Phase == phase).OrderByDescending(kv => kv.Value).ToList();
					var n = rowsIn.Sum(kv => kv.Value);
					Console.WriteLine($"  {phase,-12} {n,6} spawn(s): {(n == 0 ? "-" : string.Join(", ", rowsIn.Select(kv => $"{kv.Key.Value} x{kv.Value} ({100.0 * kv.Value / n:F1}%)")))}");
				}

				Console.WriteLine();
			}

			Console.WriteLine($"field-predictors: is `{target}` COMPUTED from something else the same spawn carries?");
			Console.WriteLine("⚠️ The purity table below is LOGIN-BURST ONLY, which is the set the importer lifts through.");
			Console.WriteLine("Purity = group the sightings by a candidate's value; within each group, the share that agree on");
			Console.WriteLine("the target. 100% = the candidate determines the target. Beat the baseline or nothing was learnt.");
			Console.WriteLine();

			if (rows.Count == 0)
			{
				Console.WriteLine($"No captured NPC spawn carries `{target}` ({skipped} spawns had it absent). "
					+ "Names come from the flattened CoreCharacterIIR - run --report field-stability for the list.");
				return;
			}

			var targetCounts = rows.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
			var baseline = 100.0 * targetCounts[0].Count() / rows.Count;
			Console.WriteLine($"{rows.Count} sightings ({skipped} skipped: the field was absent), target values: "
				+ string.Join(", ", targetCounts.Select(g => $"{g.Key} (x{g.Count()})")));
			Console.WriteLine($"BASELINE {baseline:F1}% - what a constant `{targetCounts[0].Key}` already scores.");
			Console.WriteLine();
			Console.WriteLine($"{"candidate",-44} {"purity",7} {"values",7}  mapping (pure groups only)");

			// A candidate is only comparable if it is present on every row: a predictor observed on a subset is
			// scored against a different denominator, and would sort above one that covers the whole corpus.
			var candidates = rows[0].Predictors.Keys.Where(k => rows.All(r => r.Predictors.ContainsKey(k))).ToList();
			var scored = new List<(string Name, double Purity, int Cardinality, string Mapping)>();
			foreach (var candidate in candidates)
			{
				var groups = rows.GroupBy(r => r.Predictors[candidate], StringComparer.Ordinal).ToList();
				var agreeing = groups.Sum(g => g.GroupBy(r => r.Target, StringComparer.Ordinal).Max(t => t.Count()));
				var purity = 100.0 * agreeing / rows.Count;

				// Print the mapping only when it is small enough to READ - a 500-row map is noise, and a candidate
				// that needs one is failing the coarseness test anyway.
				var mapping = groups.Count <= 6
					? string.Join(" ", groups.OrderByDescending(g => g.Count()).Select(g =>
					{
						var t = g.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(x => x.Count()).ToList();
						return $"{g.Key}=>{(t.Count == 1 ? t[0].Key : string.Join("/", t.Select(x => $"{x.Key}x{x.Count()}")))}";
					}))
					: "";
				scored.Add((candidate, purity, groups.Count, mapping));
			}

			var fineThreshold = rows.Count / 20;
			foreach (var s in scored.OrderByDescending(s => s.Purity).ThenBy(s => s.Cardinality).Take(30))
			{
				Console.WriteLine($"{(s.Name.Length > 43 ? s.Name[..43] : s.Name),-44} {s.Purity,6:F1}% {s.Cardinality,7}"
					+ $"{(s.Cardinality > fineThreshold ? " (fine)" : "       ")} {s.Mapping}");
			}

			Console.WriteLine();
			Console.WriteLine($"`(fine)` = more than {fineThreshold} distinct values over {rows.Count} sightings; its purity is an artefact of");
			Console.WriteLine("granularity, not an explanation. A real answer is a COARSE candidate at ~100%.");

			// The EXCEPTIONS to the best coarse candidate, in full. A candidate at 99.98% is either a rule with a
			// mechanism behind its exception or a coincidence, and the count alone cannot tell them apart - so print
			// every disagreeing sighting rather than its tally (the #549 lesson: a report has to let you re-cut an
			// anomaly by a field you did not think to print).
			var best = scored.Where(s => s.Cardinality <= fineThreshold).OrderByDescending(s => s.Purity).FirstOrDefault();
			if (best is { Name: not null, Purity: > 90.0 and < 100.0 })
			{
				Console.WriteLine();
				Console.WriteLine($"--- every sighting that disagrees with `{best.Name}` ({best.Purity:F2}%) ---");
				var majority = rows
					.GroupBy(r => r.Predictors[best.Name], StringComparer.Ordinal)
					.ToDictionary(g => g.Key, g => g.GroupBy(r => r.Target, StringComparer.Ordinal).OrderByDescending(t => t.Count()).First().Key, StringComparer.Ordinal);

				for (var i = 0; i < rows.Count; i++)
				{
					var predicted = majority[rows[i].Predictors[best.Name]];
					if (predicted == rows[i].Target)
						continue;

					Console.WriteLine($"  {names[i].Name} in {captureOf[i]}: {best.Name}={rows[i].Predictors[best.Name]} predicts {target}={predicted}, sent {rows[i].Target}");
					Console.WriteLine($"    movetask={rows[i].Predictors.GetValueOrDefault("[movetask].Mode")}"
						+ $" leaf={rows[i].Predictors.GetValueOrDefault("[movetask].LeafMode")}"
						+ $" buffs={rows[i].Predictors.GetValueOrDefault("[post].BuffRecords count")}"
						+ $" template={rows[i].Predictors.GetValueOrDefault("npc_stats.TemplateId")}"
						+ $" bccState={rows[i].Predictors.GetValueOrDefault("Stats.BccState")}"
						+ $" animIdle={rows[i].Predictors.GetValueOrDefault("Bcc.AnimIdleId")}"
						+ $" animAction={rows[i].Predictors.GetValueOrDefault("Bcc.AnimActionId")}");
				}
			}

			// The PLAYER family, as a cross-check on the post-IIR header word. A candidate measured on one family and
			// confirmed on the other is a protocol fact; one measured on NPCs alone could be an NPC convention.
			if (players.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"--- cross-check: PLAYER spawns, [post].MovementMode vs `{target}` ---");
				foreach (var g in players.GroupBy(p => (p.MovementMode, p.Target)).OrderByDescending(g => g.Count()))
					Console.WriteLine($"  MovementMode={g.Key.MovementMode}, {target}={g.Key.Target}: x{g.Count()}");
			}

			// Is the instability RUNTIME? `field-stability` scores a field over every sighting, so an entity caught
			// mid-action scores as "the type has no value for this". That conflates two different verdicts: a field
			// with no type-level value at all, and one whose type-level value is its RESTING value, temporarily
			// displaced by whatever the entity is doing. The two are told apart by re-running the type grouping over
			// FRESH sightings only - an empty move-task slot and no active buffs, i.e. an entity that has just been
			// spawned and is doing nothing. If the field is type-stable there, a resting value exists and the type row
			// may hold it; if it is not, no column can (Gitea #605).
			var fresh = rows
				.Select((r, i) => (Row: r, Template: r.Predictors.GetValueOrDefault("npc_stats.TemplateId", "")))
				.Where(x => x.Row.Predictors.GetValueOrDefault("[movetask].Mode") == "(empty slot)"
					&& x.Row.Predictors.GetValueOrDefault("[post].BuffRecords count") == "0"
					&& x.Template.Length > 0)
				.GroupBy(x => x.Template, StringComparer.Ordinal)
				.Where(g => g.Count() > 1)
				.ToList();

			if (fresh.Count > 0)
			{
				var stable = fresh.Count(g => g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal).Count() == 1);
				Console.WriteLine();
				Console.WriteLine($"--- `{target}` over FRESH sightings only (empty move-task slot, no buffs) ---");
				Console.WriteLine($"{stable} of {fresh.Count} templates sighted more than once fresh hold ONE value: {100.0 * stable / fresh.Count:F1}% type-stable.");
				Console.WriteLine("Compare this with the same field's figure in --report field-stability, which is over ALL sightings.");
				foreach (var g in fresh.Where(g => g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal).Count() > 1).Take(10))
					Console.WriteLine($"  UNSTABLE FRESH: template {g.Key} -> {string.Join("/", g.Select(x => x.Row.Target).Distinct(StringComparer.Ordinal))}");
			}

			// WHO sends each value. A purity table says a candidate explains the field; it cannot say what the field
			// MEANS. The NPCs behind each value can - "every sighting of value 3 is a mounted rider" is the kind of
			// statement no cross-tab produces, and it is what turns a correlation into a name.
			Console.WriteLine();
			Console.WriteLine($"--- who sends each `{target}` value ---");
			foreach (var group in names.GroupBy(n => n.Target, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
			{
				var byName = group.GroupBy(n => n.Name, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ToList();
				Console.WriteLine();
				Console.WriteLine($"{target} = {group.Key}: {group.Count()} sightings over {byName.Count} distinct NPC names");
				Console.WriteLine($"  {string.Join(", ", byName.Take(15).Select(g => $"{g.Key} (x{g.Count()})"))}");
			}
		}

		// The synthetic predictors that come from the spawn's MOVE TASK rather than from its character message. Every
		// one of these is a fact about what the entity is DOING at the instant of the spawn - which is the only kind of
		// candidate that can explain a per-spawn-event field - and the move plan is the sole such fact the body holds.
		//
		// Buckets, not raw values, on everything continuous: a duration in milliseconds partitions the corpus into one
		// group per sighting and would score a meaningless 100% (see the coarseness warning on FieldPredictors).
		private static IEnumerable<(string Name, string Value)> MoveTaskPredictors(SpawnPostIir post)
		{
			yield return ("[post].HasMoveTaskSlot", post.HasMoveTaskSlot.ToString());
			yield return ("[post].MovementMode", post.MovementMode.ToString());
			yield return ("[post].BuffRecords count", post.BuffRecords.Count.ToString());

			var task = post.MoveTask;
			yield return ("[movetask].Mode", task == null ? post.HasMoveTaskSlot ? "(empty slot)" : "(no slot)" : task.Mode.ToString());
			if (task == null)
			{
				yield return ("[movetask].PathFlags", "(none)");
				yield return ("[movetask].ByReference", "(none)");
				yield return ("[movetask].Waypoints", "(none)");
				yield return ("[movetask].HeaderFlags", "(none)");
				yield break;
			}

			// A mode-4 ListMoveTask wraps the real plan, so read the leaf: the wrapper's own mode says nothing about
			// what the entity is doing, and every list in the corpus is a plan plus its modifiers.
			var leaf = task;
			while (leaf is { Mode: 4, Children.Count: > 0 })
				leaf = leaf.Children[0];

			yield return ("[movetask].LeafMode", leaf.Mode.ToString());
			yield return ("[movetask].PathFlags", leaf.Mode == 1 ? leaf.PathFlags.ToString() : "(not a path)");
			yield return ("[movetask].ByReference", leaf.Mode == 1 ? ((leaf.PathFlags & 1) != 0).ToString() : "(not a path)");
			yield return ("[movetask].HeaderFlags", leaf.HeaderFlags.ToString());
			yield return ("[movetask].Waypoints", leaf.Mode != 1
				? "(not a path)"
				: (leaf.PathFlags & 1) != 0
					? "(by reference)"
					: (leaf.Waypoints.Count / 3) switch { 0 => "0", 1 => "1", <= 4 => "2-4", _ => "5+" });
		}

		private static List<string> FieldDiff(object? theirs, object? ours, string prefix = "", int depth = 0)
		{
			var diffs = new List<string>();
			if (depth > 3 || (theirs == null && ours == null))
				return diffs;

			if (theirs == null || ours == null)
			{
				diffs.Add($"{prefix.TrimEnd('.')} {(theirs == null ? "absent" : "present")} vs {(ours == null ? "absent" : "present")}");
				return diffs;
			}

			foreach (var prop in theirs.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
			{
				if (prop.GetIndexParameters().Length > 0)
					continue;

				object? a, b;
				try
				{
					a = prop.GetValue(theirs);
					b = prop.GetValue(ours);
				}
				catch (System.Reflection.TargetInvocationException)
				{
					continue; // protobuf-net throws on an unset field with no default; nothing to compare
				}

				var name = prefix + prop.Name;
				if (a is System.Collections.ICollection ca && b is System.Collections.ICollection cb)
				{
					if (ca.Count != cb.Count)
						diffs.Add($"{name} count {ca.Count} vs {cb.Count}");

					continue;
				}

				if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string) || prop.PropertyType.IsEnum)
				{
					if (!Equals(a, b))
						diffs.Add($"{name} {a} vs {b}");

					continue;
				}

				if (prop.PropertyType.IsClass && prop.PropertyType != typeof(byte[]))
					diffs.AddRange(FieldDiff(a, b, name + ".", depth + 1));
			}

			return diffs;
		}
	}
}

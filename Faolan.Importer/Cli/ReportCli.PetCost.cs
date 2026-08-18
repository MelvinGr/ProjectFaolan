using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Cli
{
	// `--report pet-cost`: which field of a summoned pet's SPAWN carries the point it costs? (Gitea #823.)
	//
	// The client shows every necromancer pet as costing 2 regardless of type - `Dread Archmage: Blood Arcanist`
	// should cost 4 - and the accounting is entirely client-side: stat `0x1C8 PetPoints` appears in ZERO updates
	// across all 59 necromancer captures. So the client computes the cost from something it already holds, and
	// #823's reading is that it costs the PET it receives rather than the spell that was cast.
	//
	// ⭐ The corpus can test that, which the ticket did not realise: it holds pets of THREE different cost classes -
	// 89 spawns of cost-1 creatures (Demon Warlord, Demon Familiar, Blighted One, ...), 13 of cost-2 (Arcanist,
	// Magus, Reaper) and 53 of cost-4 (Archmagus, Blood Arcanist). A field that carries the cost must read 1, 2 and 4
	// on the right ones, so this discriminates well.
	//
	// ⛔ **And nothing does.** Of the 64 numeric fields the character message carries, 60 are present on all 155 pet
	// spawns and 29 of those actually vary - and NOT ONE equals the pet's cost. `generic_stats` is empty on every
	// pet. So the pet's spawn body does not carry its cost, which REFUTES this ticket's central reading: the client
	// cannot be costing "the pet it receives" from anything in the pet's own spawn.
	//
	// ⚠️ Two creature types differ in a great many fields, so a raw diff is noise. The test is "does the field EQUAL
	// the cost", never "does it differ" - the latter would name dozens. And the counts above are printed as a control
	// on the sweep itself, because "no field equals the cost" and "the sweep collected no fields" print the same.
	public static partial class ReportCli
	{
		// Walks a protobuf message's properties and records every integral value, one row per (field path, sample).
		// Depth-limited: the message nests, and a cost is not going to be five levels down.
		private static void CollectNumeric(object? message, string prefix, uint cost, List<(string Field, uint Cost, ulong Value)> into, int depth)
		{
			if (message == null || depth > 2)
				return;

			foreach (var property in message.GetType().GetProperties())
			{
				object? value;
				try
				{
					value = property.GetValue(message);
				}
				catch
				{
					continue; // a protobuf accessor can throw on an unset one-of member; it carries nothing either way
				}

				if (value == null)
					continue;

				var name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
				switch (value)
				{
					case uint u: into.Add((name, cost, u)); break;
					case int i when i >= 0: into.Add((name, cost, (ulong)i)); break;
					case ulong ul: into.Add((name, cost, ul)); break;
					case long l when l >= 0: into.Add((name, cost, (ulong)l)); break;
					case ushort us: into.Add((name, cost, us)); break;
					case byte b: into.Add((name, cost, b)); break;
					case string: break;
					default:
						if (value.GetType().Namespace?.StartsWith("Faolan", StringComparison.Ordinal) == true)
							CollectNumeric(value, name, cost, into, depth + 1);

						break;
				}
			}
		}

		/// <summary>
		///     The THIRD place a cost could live, and the only one that is a message WE build: the
		///     <c>PetIIR</c> owner-assign's <c>(f1, f2, f3)</c> KIND triple (#823).
		/// </summary>
		/// <remarks>
		///     ⭐ The reason to look. <c>PetCodec</c> carries the triple as two named constants -
		///     <c>CombatKind = (0, 2, 2)</c> and <c>SocialKind = (5, 0, 1)</c> - and its own comment says where they
		///     come from: <i>"the capture shows a COMBAT pet = (0,2,2) and a SOCIAL pet = (5,0,1)"</i>. Two capture
		///     rows, generalised to every pet. So the server sends a literal <b>2</b> in two of the three slots for
		///     every combat pet it ever summons, and the symptom this ticket opened on is that <b>every pet costs
		///     2</b>. That coincidence is worth measuring before anything else.
		///     <para />
		///     The test is the corpus: resolve each assign's PET HANDLE to the creature it spawned as, and cross-tab
		///     the triple against that creature's cost. If a slot is the cost it reads 1, 2 and 4 on the right ones.
		///     <para />
		///     ⚠️ A miss here is weaker than a hit. The triple could be a UI slot or a command set and the client
		///     could still cost from its own RDB - so "no slot equals the cost" leaves the constant unjustified
		///     rather than exonerated, which is why the observed triples are printed either way.
		/// </remarks>
		private static void PetAssignKinds(IReadOnlyList<NamedCapture> captures, Dictionary<string, uint> costOf)
		{
			Console.WriteLine();
			Console.WriteLine("  ⭐ the PetIIR owner-assign's (f1,f2,f3) KIND triple, against the pet's cost (#823):");

			var rows = new List<(string Creature, uint Cost, uint F1, uint F2, uint F3)>();
			var assigns = 0;
			var unresolved = 0;
			foreach (var c in captures)
			{
				// Handle -> creature name, from the same capture's spawns. A pet's assign names it only by handle, so
				// the spawn is the only thing that says WHICH creature was bound.
				var nameOf = new Dictionary<uint, string>();
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
				{
					if (e.Name != null)
						nameOf[e.Handle] = e.Name;
				}

				foreach (var p in c.Packets)
				{
					if (p is not { IsIirCarrier: true, SubOpcode: PetCodec.Opcode })
						continue;

					if (!p.TryParse(PetCodec.Parse, out var pet) || pet.Mode != PetCodec.ModeAssign)
						continue;

					assigns++;
					if (!nameOf.TryGetValue(pet.Pet, out var creature) || !costOf.TryGetValue(creature, out var cost))
					{
						unresolved++;
						continue;
					}

					rows.Add((creature, cost, pet.Kind.F1, pet.Kind.F2, pet.Kind.F3));
				}
			}

			// ⚠️ The control, first: "no slot carries the cost" and "no assign was resolved" print the same otherwise.
			Console.WriteLine($"    control - {assigns} owner-assign(s) captured, {rows.Count} resolved to a costed creature ({unresolved} unresolved)");
			if (rows.Count == 0)
			{
				Console.WriteLine("    ⛔ nothing to test: no captured assign resolves to a creature with a known cost.");
				return;
			}

			foreach (var g in rows.GroupBy(r => r.Creature).OrderBy(g => g.Key))
				Console.WriteLine($"      {g.Key,-20} cost {g.First().Cost}  triples: {string.Join(" ", g.GroupBy(r => (r.F1, r.F2, r.F3)).Select(t => $"({t.Key.F1},{t.Key.F2},{t.Key.F3})x{t.Count()}"))}");

			foreach (var g in rows.GroupBy(r => (r.F1, r.F2, r.F3)).OrderBy(g => g.Key.F1).ThenBy(g => g.Key.F2))
			{
				var costs = string.Join(", ", g.GroupBy(r => r.Cost).OrderBy(x => x.Key).Select(x => $"cost {x.Key} x{x.Count()}"));
				Console.WriteLine($"    ({g.Key.F1},{g.Key.F2},{g.Key.F3})  {g.Count(),3} assign(s)  [{costs}]  {string.Join(", ", g.Select(r => r.Creature).Distinct())}");
			}

			foreach (var (slot, get) in new (string Slot, Func<(string Creature, uint Cost, uint F1, uint F2, uint F3), uint> Get)[]
			         { ("f1", r => r.F1), ("f2", r => r.F2), ("f3", r => r.F3) })
			{
				var equal = rows.Count(r => get(r) == r.Cost);
				Console.WriteLine($"    {slot} equals the cost on {equal}/{rows.Count}"
				                  + (equal == rows.Count ? "  ⭐ CARRIES IT" : rows.Select(get).Distinct().Count() == 1 ? "  (constant across the corpus)" : ""));
			}
		}

		/// <summary>
		///     The second sweep: which fields DETERMINE the cost, as opposed to carrying it (#823).
		/// </summary>
		/// <remarks>
		///     ⭐ The first sweep asks "does this field EQUAL the cost", and its negative is real but narrower than it
		///     reads. If the client looks a cost UP - by the summoning spell, by the pet's template - then the field
		///     it reads carries a KEY, and a key does not equal what it resolves to. That sweep would miss it, and
		///     the refutation it produced ("the pet's spawn does not carry its cost") is only about the value.
		///     <para />
		///     So: group the samples by the field's value and ask whether every group is one cost. That is what a key
		///     looks like.
		///     <para />
		///     ⚠️ <b>Purity alone would name dozens</b>, and the reason is the trap this repo has hit before: a field
		///     that is nearly UNIQUE per sample determines everything for free (a handle, a position, a timestamp).
		///     The distinct-value count is printed beside every hit and the interpretation is stated with it - a field
		///     with as many values as there are CREATURES is the creature's identity, which is a real answer; a field
		///     with as many values as there are SAMPLES is telling you nothing.
		/// </remarks>
		private static void DeterminesTheCost(List<(string Field, uint Cost, ulong Value)> numeric, int samples)
		{
			Console.WriteLine();
			Console.WriteLine("  ⭐ which fields DETERMINE the cost (a KEY the client could look a cost up by), rather than carrying it:");

			var hits = new List<(string Field, int Distinct, string Shape)>();
			foreach (var g in numeric.GroupBy(n => n.Field))
			{
				var rows = g.ToList();
				if (rows.Count < samples)
					continue;

				var byValue = rows.GroupBy(r => r.Value).ToList();
				if (byValue.Any(v => v.Select(r => r.Cost).Distinct().Count() > 1))
					continue; // one value, two costs - it cannot be the key

				var distinct = byValue.Count;
				if (distinct < 2)
					continue; // a constant determines nothing; it is the base rate wearing a purity score

				// ⚠️ The whole judgement is here, and it is about the VALUE COUNT rather than the purity.
				var shape = distinct >= rows.Count
					? "⛔ one value per SAMPLE - determines everything for free, says nothing"
					: distinct <= 4
						? "⭐ few values - a cost CLASS or the creature's kind"
						: "one value per creature-ish - an identity the client could key on";
				hits.Add((g.Key, distinct, shape));
			}

			if (hits.Count == 0)
			{
				Console.WriteLine("    ⛔ NONE - no field partitions the samples by cost at all, so the client is not keying off the spawn.");
				return;
			}

			foreach (var (field, distinct, shape) in hits.OrderBy(h => h.Distinct).ThenBy(h => h.Field))
			{
				Console.WriteLine($"    {field,-46} {distinct,4} distinct value(s)   {shape}");

				// ⭐ The MAPPING, for the ones small enough to read. A key is only actionable once you can see what
				// retail put in it - that is what says whether OUR spawn sends the same value.
				if (distinct > 24)
					continue;

				foreach (var v in numeric.Where(n => n.Field == field).GroupBy(n => n.Value).OrderBy(v => v.Key))
					Console.WriteLine($"        {v.Key,12} -> cost {v.First().Cost}   ({v.Count()} sample(s))");
			}
		}

		private static async Task<int> PetCost(IReadOnlyList<NamedCapture> captures)
		{
			await using var db = OpenDb();

			// creature NAME -> the point cost of the spell that summons it. The link is the summon spell's own
			// `SummonSpawnHash`, but the names are what a spawn body carries, so the join is by name here and the
			// costs are quoted from the spell rows.
			var costOf = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
			foreach (var spell in await db.Spells.AsNoTracking().Where(s => s.SummonPointCost != null && s.SummonPointCost > 0).ToListAsync())
			{
				// "Dread Archmage: Blood Arcanist" summons the creature "Blood Arcanist" - the creature is the part
				// after the colon where there is one.
				var name = spell.Name ?? "";
				var creature = name.Contains(':', StringComparison.Ordinal) ? name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim() : name;
				if (creature.Length > 0)
					costOf[creature] = spell.SummonPointCost!.Value;
			}

			var seen = new List<(string Name, uint Cost, uint Level, uint[] Stats)>();

			// ⭐ Every NUMERIC field of the character message, by name, so the sweep is not limited to the one array
			// I guessed at. A per-entity cost has to be SOME number the body carries, and reflection asks all of them
			// rather than requiring the right guess up front.
			var numeric = new List<(string Field, uint Cost, ulong Value)>();
			var spawns = 0;
			foreach (var c in captures)
			foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
			{
				spawns++;
				if (e.Name == null || !costOf.TryGetValue(e.Name, out var cost))
					continue;

				var body = e.Data is { Length: >= 12 } ? e.Data[(12 + (int)e.Data.ReadU32Be(8))..] : null;
				if (body == null || !SpawnBodyReader.TryReadCharacterMessage(body, out var read))
					continue;

				seen.Add((e.Name, cost, read.Message.Stats?.Level ?? 0, read.Message.GenericStats ?? []));
				CollectNumeric(read.Message, "", cost, numeric, 0);
			}

			Console.WriteLine("=== which spawn field carries a pet's POINT COST? (#823) ===");
			Console.WriteLine();
			Console.WriteLine($"  control - {spawns} NPC spawn(s) extracted, {seen.Count} of them a summonable pet with a known cost");

			var byCost = seen.GroupBy(s => s.Cost).OrderBy(g => g.Key).ToList();
			foreach (var g in byCost)
				Console.WriteLine($"    cost {g.Key}: {g.Count()} spawn(s) - {string.Join(", ", g.Select(s => s.Name).Distinct())}");

			Console.WriteLine();
			if (byCost.Count < 2)
			{
				// ⛔ The answer #823 needs and the corpus may not have. One cost class cannot discriminate: every
				// candidate field reads the same on every sample, so nothing can be ruled in OR out.
				Console.WriteLine("  ⛔ Fewer than TWO cost classes are captured, so nothing here can discriminate: a field that");
				Console.WriteLine("     carried the cost and a field that was constant would look identical. This needs a capture of");
				Console.WriteLine("     a pet whose cost differs from the ones already held.");
				return 1;
			}

			// ⭐ THE TEST. A field carrying the cost reads the cost - so score each stat SLOT by whether its value
			// equals the pet's own cost on every sample that has it.
			var width = seen.Max(s => s.Stats.Length);
			Console.WriteLine($"  generic_stats is up to {width} slot(s) wide. A slot carrying the cost equals it on EVERY sample:");
			var carriers = 0;
			for (var slot = 0; slot < width; slot++)
			{
				var have = seen.Where(s => s.Stats.Length > slot).ToList();
				if (have.Count < seen.Count)
					continue;

				var matches = have.Count(s => s.Stats[slot] == s.Cost);
				var distinct = have.Select(s => s.Stats[slot]).Distinct().Count();
				if (matches != have.Count)
					continue;

				carriers++;
				Console.WriteLine($"    ⭐ slot {slot}: equals the cost on {matches}/{have.Count}, {distinct} distinct value(s)");
			}

			if (carriers == 0)
			{
				Console.WriteLine("    ⛔ NONE. No generic_stats slot carries the cost, so the client is not reading it from there.");
				Console.WriteLine();
				Console.WriteLine("    the raw arrays, so the next reader can look rather than re-extract:");
				foreach (var g in byCost)
				{
					foreach (var s in g.Take(2))
						Console.WriteLine($"      cost {s.Cost,2}  level {s.Level,3}  {s.Name,-18} [{string.Join(", ", s.Stats.Take(24))}]");
				}
			}

			// ⭐ The general sweep: every numeric field the message carries, scored the same way. A field that is the
			// cost equals it on every sample; anything less is not the field.
			Console.WriteLine();
			// ⚠️ The control on the sweep itself. "No field equals the cost" and "the sweep collected no fields" are
			// the same printed answer, and only one of them is a finding.
			var groups = numeric.GroupBy(n => n.Field).ToList();
			var complete = groups.Count(g => g.Count() >= seen.Count);
			var varying = groups.Count(g => g.Count() >= seen.Count && g.Select(r => r.Value).Distinct().Count() > 1);
			Console.WriteLine($"  every NUMERIC field of the character message: {groups.Count} distinct field(s) collected,"
			                  + $" {complete} present on all {seen.Count} samples, {varying} of those actually VARY (a constant cannot be three costs):");
			var found = 0;
			foreach (var g in numeric.GroupBy(n => n.Field))
			{
				var rows = g.ToList();
				if (rows.Count < seen.Count || rows.All(r => r.Value == rows[0].Value))
					continue; // absent from some samples, or constant - a constant cannot be three different costs

				if (rows.All(r => r.Value == r.Cost))
				{
					found++;
					Console.WriteLine($"    ⭐ {g.Key}: equals the cost on all {rows.Count} samples");
				}
			}

			if (found == 0)
				Console.WriteLine("    ⛔ NONE - no numeric field of the spawn equals the pet's cost on every sample.");

			Console.WriteLine();
			Console.WriteLine("  ⚠️ A pet's spawn is a CREATURE spawn: two pet types differ in a great many fields, so the test is");
			Console.WriteLine("     'does the field EQUAL the cost', never 'does the field differ'. The latter would name dozens.");

			DeterminesTheCost(numeric, seen.Count);
			PetAssignKinds(captures, costOf);
			return 0;
		}
	}
}

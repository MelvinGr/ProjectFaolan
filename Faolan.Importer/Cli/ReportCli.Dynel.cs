using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	public static partial class ReportCli
	{
		// Per capture, the named NPC's persistent idle animation (CharacterBcc.AnimIdleId) from its FA015807.
		private static void Anim(IReadOnlyList<NamedCapture> captures, string target)
		{
			foreach (var c in captures)
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets).Where(e => e.Name == target))
				{
					var body = e.Data is { Length: >= 12 }
						? e.Data[(12 + (int)e.Data.ReadU32Be(8))..]
						: null;
					var anim = body != null && SpawnBodyReader.TryReadCharacterMessage(body, out var read)
						? read.Message?.Bcc?.AnimIdleId.ToString() ?? "(no bcc)"
						: "(unreadable)";
					Console.WriteLine($"{c.Name[..Math.Min(40, c.Name.Length)]} | handle 0x{e.Handle:X} | AnimIdleId={anim} | pos=({e.X:0.#},{e.Z:0.#})");
				}
		}

		// #571: how often a single capture reuses one entity handle for MORE THAN ONE NPC, and what the spawn import
		// loses to it. Handles are recycled - a handle freed by a despawn is reissued later in the same session - but
		// the import treats a handle as a capture-scoped mob identity: `DistinctByHandle` keeps the FIRST spawn per
		// handle and silently drops every later one, and the patrol join used to hand the survivor the other NPC's
		// route (fixed in #565 by a positional sanity check, which is where this was found).
		//
		// ⚠️ Only a change of TEMPLATE proves it. The obvious-looking test - the same handle seen at two distant
		// positions - does NOT: an NPC that WANDERS or patrols streams in again wherever it has walked to, and over a
		// long session a cow covers a couple of hundred units. Counting distance-apart-only reuses reported 229 handles
		// where the provable figure is far smaller, and every inflated one was a Wild Cow / Conarch Guard seen at three
		// points of its own route. So distance is reported SEPARATELY as ambiguous and is never added to the total: a
		// different template on one handle is the only thing that cannot be one mob moving.
		private static void HandleRecycling(IReadOnlyList<NamedCapture> captures)
		{
			var provenHandles = 0;
			var provenLost = 0;
			var ambiguousHandles = 0;
			foreach (var capture in captures)
			{
				var byHandle = new Dictionary<uint, List<NpcSpawnEntry>>();
				foreach (var e in NpcSpawnExtractor.Extract(capture.Packets))
				{
					if (!byHandle.TryGetValue(e.Handle, out var list))
						byHandle[e.Handle] = list = [];

					if (!list.Exists(p => p.TemplateId == e.TemplateId))
						list.Add(e); // one entry per DISTINCT template on this handle
				}

				var proven = byHandle.Where(kv => kv.Value.Count > 1).ToList();
				var crossMap = proven.Count(kv => kv.Value.Select(e => e.MapId).Distinct().Count() > 1);
				if (proven.Count > 0)
					Console.WriteLine($"  ({crossMap} of {proven.Count} span more than one MAP)");
				ambiguousHandles += byHandle.Count(kv => kv.Value.Count == 1);
				if (proven.Count == 0)
					continue;

				var lost = proven.Sum(kv => kv.Value.Count - 1); // DistinctByHandle keeps the first, drops the rest
				provenHandles += proven.Count;
				provenLost += lost;
				Console.WriteLine($"{capture.Name[..Math.Min(60, capture.Name.Length)]} | handles proven recycled: {proven.Count} | spawns dropped: {lost}");
				foreach (var (handle, seen) in proven.OrderByDescending(kv => kv.Value.Count))
				{
					var who = seen.Select(e => $"map {e.MapId}: {e.Name ?? "?"}#{e.TemplateId}@({e.X:0.#},{e.Z:0.#})");
					Console.WriteLine($"    0x{handle:X8} -> {string.Join("  |  ", who)}");
				}
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL over {captures.Count} captures: {provenHandles} handle(s) PROVEN recycled (a second template on the same handle), "
			                  + $"costing {provenLost} spawn observation(s) to DistinctByHandle. {ambiguousHandles} single-template handle(s) were not counted - "
			                  + "a repeat sighting of one template is a mob that moved, not evidence of reuse.");
		}

		// --report dynel-cycle [--report-arg <minimum burst size, default 4>]
		//
		// Retail's world-object interest, measured: every BURST of `SimpleDynelFullUpdateIIR` spawns or
		// `n3ToClientQuitIIR` despawns that arrives in one second, with the burst's centroid, the player's own
		// position at that moment (from their last uploaded `CharDCMove`) and the distance between the two.
		//
		// #529 is the question it answers. Twelve Stone Bricks were seen despawning at 83u, respawning at 144u and
		// despawning again at 149u - an ordering no radius test can produce, so whatever drives retail's streaming is
		// not the distance our `InterestManager` uses. One capture cannot say what it IS; a census of every bulk
		// event in the corpus can, because a RADIUS leaves a tight band of distances and a grid/cell scheme does not.
		//
		// Bursts, not individual events, because that is the unit of the phenomenon: an interest pass moves a whole
		// group at once, and a single spawn is just a mob walking into view.
		// --report dynel-effects
		//
		// Every `SimpleDynelVisualChangeIIR` PARTICLE record in the corpus, attributed to the object it played on and
		// crossed against the effect that object's own geometry declares (#698/#687). Written because the two
		// questions this family raises cannot be answered from a count: whether a record is a standing decoration or
		// a one-off puff (the ATTRACTOR says), and whether we already hold its effect (the geometry node says).
		//
		// ⚠️ Handles are RECYCLED - the same handle is a `Location Object - Walls` early in a capture and an Oak Tree
		// later - so a record is attributed through the SPAWN that most recently minted its handle, within one
		// capture. Keying on the handle alone merges unrelated objects and was worth an hour on #494.
		private static void DynelEffects(IReadOnlyList<NamedCapture> captures, DatabaseContext db)
		{
			// What the objects' own geometry declares: effect id -> how many nodes carry it (#687).
			var declared = db.WorldObjectWords
				.Where(w => w.Kind == WorldObjectWordKind.GeometryNode && w.Ordinal % 6 == 0)
				.AsEnumerable()
				.GroupBy(w => w.Value)
				.ToDictionary(g => g.Key, g => g.Count());

			var starts = new List<(string Capture, uint Handle, string Name, uint Effect, uint Attractor, bool AfterSpawn)>();
			var stopped = new List<(string Capture, uint Handle, string Name, uint TypeTag, uint Word, bool HadStart)>();
			foreach (var capture in captures)
			{
				var namesByHandle = new Dictionary<uint, string>();
				var spawnedInThisCapture = new HashSet<uint>();
				foreach (var p in capture.Packets)
				{
					if (!p.IsIirCarrier)
						continue;

					// The spawn that minted the handle - and it re-mints, so the LAST one before a record wins. Every
					// class of the dynel family carries the same identity message in the same place, which is why one
					// codec read plus WorldObjectColumns covers all of them.
					if (WorldObjectClasses.Placed.Contains(p.SubOpcode) && p.TryParse(WorldObjectCodec.Parse, out var obj))
					{
						namesByHandle[obj.Handle] = WorldObjectColumns.TryReadIdentity(p.Record, out var identity) ? identity.Name ?? "" : "";
						spawnedInThisCapture.Add(obj.Handle);
						continue;
					}

					if (p.SubOpcode != SimpleDynelVisualChangeCodec.Opcode || !p.TryParse(SimpleDynelVisualChangeCodec.Parse, out var change))
						continue;

					if (change.Kind == (uint)DynelVisualChange.StopParticleEffect)
					{
						stopped.Add((capture.Name, change.Handle, namesByHandle.GetValueOrDefault(change.Handle, "<no spawn seen>"), change.TypeTag,
							change.Words.Count > 0 ? change.Words[0] : 0, starts.Any(x => x.Handle == change.Handle && x.Capture == capture.Name)));
					}

					if (change.Kind != (uint)DynelVisualChange.StartParticleEffect || change.Words.Count < 2)
						continue;

					starts.Add((capture.Name, change.Handle, namesByHandle.GetValueOrDefault(change.Handle, "<no spawn seen>"),
						change.Words[0], change.Words[1], spawnedInThisCapture.Contains(change.Handle)));
				}
			}

			Console.WriteLine($"{starts.Count} StartParticleEffect record(s), {stopped.Count} Stop, over {captures.Count} capture(s)");
			Console.WriteLine();
			Console.WriteLine("  attractor 0 = FREE-STANDING at the given position (the harvest puff); 34/35 = bolted to a bone (a standing decoration).");
			foreach (var g in starts.GroupBy(s => s.Attractor).OrderByDescending(g => g.Count()))
				Console.WriteLine($"    attractor {g.Key,3}: {g.Count(),4} record(s), {g.Select(s => s.Effect).Distinct().Count()} distinct effect(s)");

			Console.WriteLine();
			Console.WriteLine("  effect        records  attractor  in a geometry node?  played on");
			foreach (var g in starts.GroupBy(s => s.Effect).OrderByDescending(g => g.Count()))
			{
				var held = declared.TryGetValue(g.Key, out var nodes) ? $"YES ({nodes} node(s))" : "no";
				var on = string.Join(", ", g.Select(s => s.Name).Distinct().Take(3));
				Console.WriteLine($"  {g.Key,-10} {g.Count(),8}  {string.Join("/", g.Select(s => s.Attractor).Distinct()),9}  {held,-19}  {on}");
			}

			// The STOPs, with what they played on. A Stop that never had a Start in the same capture is retail
			// clearing an effect the object brought with it - which is the shape a loot bag's sparkle would have.
			Console.WriteLine();
			Console.WriteLine("  StopParticleEffect, by the object it was sent to:");
			foreach (var g in stopped.GroupBy(x => (x.TypeTag, x.Name)).OrderByDescending(g => g.Count()))
				Console.WriteLine($"    class 0x{g.Key.TypeTag:X4}  {g.Key.Name,-40} {g.Count(),3} record(s), {g.Count(x => x.HadStart)} of them after a Start in the same capture");

			// ⭐ The cross-tab that says what an effect IS. A decoration is started and left running; a STATE marker
			// is started and later stopped on the same handle. Crossed against whether the object's own geometry
			// declares the effect, the two populations separate.
			Console.WriteLine();
			Console.WriteLine("  effect      starts  stopped-on-the-same-handle  declared-in-geometry");
			foreach (var g in starts.GroupBy(x => x.Effect).OrderByDescending(g => g.Count()))
			{
				var handles = g.Select(x => (x.Capture, x.Handle)).ToHashSet();
				var alsoStopped = stopped.Count(x => handles.Contains((x.Capture, x.Handle)));
				Console.WriteLine($"  {g.Key,-10} {g.Count(),6}  {alsoStopped,26}  {(declared.ContainsKey(g.Key) ? "yes" : "NO"),20}");
			}

			var missing = starts.Where(s => s.Attractor != 0 && !declared.ContainsKey(s.Effect)).ToList();
			Console.WriteLine();
			Console.WriteLine($"  STANDING effects with no geometry node of ours: {missing.Select(s => s.Effect).Distinct().Count()} distinct over {missing.Count} record(s) (#698)");
			Console.WriteLine($"  of those records, {missing.Count(s => s.AfterSpawn)} played on an object whose SPAWN is in the same capture");
		}

		private static void DynelCycle(IReadOnlyList<NamedCapture> captures, string? arg)
		{
			var minimumBurst = int.TryParse(arg, out var m) && m > 0 ? m : 4;
			var spawnDistances = new List<double>();
			var despawnDistances = new List<double>();
			// The WORLD-ENTRY burst is a world BUILD, not an interest pass - it states the playfield the player just
			// loaded into, so it reaches as far as the playfield does. Scored separately, because leaving it in is
			// what makes the radius verdict read as "something spawned 1834u away" when nothing was streaming at all.
			var midSessionSpawnDistances = new List<double>();
			var midSessionDespawnDistances = new List<double>();
			var refreshBursts = 0;
			var despawnBursts = 0;
			var playerAtBurst = new List<Vector3>();
			var playerEverywhere = new List<Vector3>();

			foreach (var capture in captures)
			{
				// A dynel despawn names only a handle, so its position has to come from the spawn that introduced it.
				var positions = new Dictionary<uint, Vector3>();
				var player = Vector3.Zero;
				var seenPlayer = false;
				var bursts = new List<(long Ticks, bool Spawn, List<uint> Handles, Vector3 Player)>();

				foreach (var p in capture.Packets)
				{
					if (!p.IsIirCarrier)
						continue;

					// The player's own position: their UPLOADED moves, which is the one CharDCMove stream that is
					// certainly the capturing character (the downloads are every visible mover).
					if (p is { IsDownload: false, SubOpcode: CharDCMoveCodec.Opcode })
					{
						if (p.TryParse(CharDCMoveCodec.Parse, out var move))
						{
							player = move.Position;
							seenPlayer = true;
							playerEverywhere.Add(player); // the control sample for the cell-boundary fit below
						}

						continue;
					}

					if (!p.IsDownload || !seenPlayer)
						continue;

					uint handle;
					bool spawn;
					if (p.SubOpcode == WorldObjectClasses.SimpleDynel)
					{
						if (!p.TryParse(WorldObjectCodec.Parse, out var obj))
							continue;

						handle = obj.Handle;
						positions[handle] = obj.Transform.FromProtobuf<ProtoGameCodeDynelIIR>().Position;
						spawn = true;
					}
					else if (p.SubOpcode == N3ToClientQuitCodec.Opcode)
					{
						if (!p.TryParse(N3ToClientQuitCodec.Parse, out var quit) || !positions.ContainsKey(quit.Handle))
							continue; // only the dynels we saw spawn - a character quit is a different event

						handle = quit.Handle;
						spawn = false;
					}
					else
					{
						continue;
					}

					var last = bursts.Count > 0 ? bursts[^1] : default;
					if (bursts.Count > 0 && last.Spawn == spawn && TimeSpan.FromTicks(p.Ticks - last.Ticks).TotalSeconds <= 1.0)
						last.Handles.Add(handle);
					else
						bursts.Add((p.Ticks, spawn, [handle], player));
				}

				var interesting = bursts.Where(b => b.Handles.Count >= minimumBurst).ToList();
				if (interesting.Count == 0)
					continue;

				// A despawn burst is a REFRESH when most of what it dropped comes straight back. #529 read the Stone
				// Bricks' "removed and restored together" as an interest cycle; if the same handles return within
				// seconds, it is not interest at all - it is the cell being re-sent, and the distances at each end are
				// just wherever the player had walked to by then.
				var refreshWindow = TimeSpan.FromSeconds(30).Ticks;
				foreach (var burst in interesting.Where(b => !b.Spawn))
				{
					var dropped = burst.Handles.ToHashSet();
					var returned = interesting
						.Where(b => b.Spawn && b.Ticks > burst.Ticks && b.Ticks - burst.Ticks <= refreshWindow)
						.SelectMany(b => b.Handles)
						.ToHashSet();
					returned.IntersectWith(dropped);
					despawnBursts++;
					if (returned.Count * 2 >= dropped.Count)
						refreshBursts++;
				}

				Console.WriteLine($"{capture.Name}");
				var start = interesting[0].Ticks;
				foreach (var burst in interesting)
				{
					var known = burst.Handles.Where(positions.ContainsKey).Select(h => Vector3.Distance(positions[h], burst.Player)).ToList();
					if (known.Count == 0)
						continue;

					// The RADIUS test is one-sided per kind, and that is the whole trick. A radius R bounds a SPAWN
					// burst from above (nothing past R may appear) and a DESPAWN burst from below (nothing inside R
					// may vanish). So the numbers that matter are the FARTHEST thing that spawned and the NEAREST
					// thing that despawned - if those two ranges overlap, no single R can produce the corpus.
					var extreme = burst.Spawn ? known.Max() : known.Min();
					(burst.Spawn ? spawnDistances : despawnDistances).Add(extreme);
					if (burst.Ticks != start)
						(burst.Spawn ? midSessionSpawnDistances : midSessionDespawnDistances).Add(extreme);
					playerAtBurst.Add(burst.Player);

					Console.WriteLine($"  t+{TimeSpan.FromTicks(burst.Ticks - start).TotalSeconds,7:0.0}s  {(burst.Spawn ? "SPAWN  " : "DESPAWN")}"
					                  + $" x{burst.Handles.Count,3}  player=({burst.Player.X,8:0.#},{burst.Player.Z,8:0.#})"
					                  + $"  {(burst.Spawn ? "farthest spawned" : "nearest despawned")}={extreme,7:0.#}"
					                  + $"  (near={known.Min(),7:0.#} far={known.Max(),7:0.#})");
				}
			}

			Console.WriteLine();
			Report("SPAWN   farthest-spawned", spawnDistances);
			Report("DESPAWN nearest-despawned", despawnDistances);
			Console.WriteLine();
			Console.WriteLine("Excluding each capture's WORLD-ENTRY burst (a world build, not an interest pass):");
			Report("SPAWN   farthest-spawned", midSessionSpawnDistances);
			Report("DESPAWN nearest-despawned", midSessionDespawnDistances);
			if (despawnBursts > 0)
			{
				Console.WriteLine();
				Console.WriteLine($"REFRESH: {refreshBursts} of {despawnBursts} despawn burst(s) ({100.0 * refreshBursts / despawnBursts:0.#}%) had most of what they");
				Console.WriteLine("  dropped re-spawned within 30s - i.e. the cell was RE-SENT rather than the objects losing interest.");
			}

			Console.WriteLine();
			Console.WriteLine("A single interest RADIUS R would need every 'farthest spawned' <= R <= every 'nearest despawned'.");
			if (spawnDistances.Count > 0 && despawnDistances.Count > 0)
			{
				var floor = despawnDistances.Min();
				var ceiling = spawnDistances.Max();
				Console.WriteLine(floor >= ceiling
					? $"  -> consistent: any R in [{ceiling:0.#}, {floor:0.#}] fits."
					: $"  -> IMPOSSIBLE: something spawned at {ceiling:0.#} while something else despawned at {floor:0.#}. "
					  + $"{spawnDistances.Count(d => d > floor)} spawn burst(s) reach past the nearest despawn.");
			}

			// The grid hypothesis #529 proposes. If interest is per CELL, a burst fires when the player CROSSES a cell
			// boundary, so the player's position at a burst should sit near a multiple of the cell size on one axis.
			//
			// ⚠️ The null model is NOT `cell/6` (the value a uniformly placed player would give). It is the SAME
			// statistic over every position the player was ever at, because a player does not walk uniformly: they
			// stand on designer-placed round coordinates, and a burst-only score has no meaning without that control.
			// A real cell size beats the walk; a spurious one matches it.
			Console.WriteLine();
			Console.WriteLine("Cell-boundary fit - player position at each burst, CONTROLLED against every position they walked:");
			foreach (var cell in new[] { 16f, 32f, 50f, 64f, 100f, 128f, 160f, 200f, 256f, 320f, 512f })
			{
				var atBursts = Median(playerAtBurst, cell);
				var whileWalking = Median(playerEverywhere, cell);
				if (double.IsNaN(atBursts) || double.IsNaN(whileWalking))
					continue;

				Console.WriteLine($"  cell {cell,5:0}: median offset at a burst {atBursts,7:0.#}   while walking {whileWalking,7:0.#}"
				                  + (atBursts < whileWalking * 0.6 ? "   <- FIT" : ""));
			}

			return;

			// DISTINCT positions on both sides: bursts arrive in clumps at one standing position (a map change fires
			// several at the same coordinates), and counting that position once per burst would let a single spot
			// carry the whole statistic.
			static double Median(List<Vector3> points, float cell)
			{
				var offsets = points
					.Distinct()
					.Select(p => Math.Min(BoundaryDistance(p.X, cell), BoundaryDistance(p.Z, cell)))
					.OrderBy(d => d)
					.ToList();
				return offsets.Count == 0 ? double.NaN : offsets[offsets.Count / 2];
			}

			static float BoundaryDistance(float value, float cell)
			{
				var offset = value - MathF.Floor(value / cell) * cell;
				return MathF.Min(offset, cell - offset);
			}

			static void Report(string kind, List<double> distances)
			{
				var usable = distances.Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
				if (usable.Count == 0)
				{
					Console.WriteLine($"{kind}: no bursts.");
					return;
				}

				Console.WriteLine($"{kind}: {usable.Count} burst(s), min={usable[0]:0.#} "
				                  + $"p25={usable[usable.Count / 4]:0.#} median={usable[usable.Count / 2]:0.#} "
				                  + $"p75={usable[usable.Count * 3 / 4]:0.#} max={usable[^1]:0.#}");
			}
		}

		// --report shield-rose
		//
		// Every `CombatShieldIIR` in the corpus as a per-defender timeline: when each rose arrived, what it said, how
		// long since that handle's previous rose, and how long since that handle's SPAWN (`SimpleCharFullUpdateIIR`)
		// in the same capture.
		//
		// The last column is the whole point (#622). Retail leaves the spawn scaffold's shield word at 0, so a viewer
		// that arrives after a defender's rose was set has nothing to draw - unless retail sends a CATCH-UP rose when
		// the entity comes into view. If it does, this report shows it as a rose arriving within a second or two of
		// that handle's spawn; if the only roses are ones that follow a previous rose by the measured 5-12s shift
		// cadence, retail has the same hole and inventing a catch-up send would be inventing a message.
		//
		// A rose that repeats the PREVIOUS VALUE is called out for the same reason: a shift moves exactly one shield
		// (34 of 39 captured transitions), so an unchanged re-send is not a shift and wants explaining.
		private static void ShieldRose(IReadOnlyList<NamedCapture> captures)
		{
			var total = 0;
			var afterSpawn = 0;
			var unchanged = 0;
			var noSpawnSeen = 0;
			foreach (var capture in captures)
			{
				var spawnTicks = new Dictionary<uint, long>();
				var previous = new Dictionary<uint, (long Ticks, uint Value)>();
				var lines = new List<string>();

				foreach (var p in capture.Packets)
				{
					if (p is not { IsIirCarrier: true, IsDownload: true })
						continue;

					// Both records put the subject handle at the same place - body is [u32 recordLen][u32 type][u32 ...]
					// [u32 handle] - so the spawn side needs no full parse (most spawn bodies would not survive one).
					if (p.SubOpcode == SimpleCharFullUpdateCodec.Opcode)
					{
						if (p.Body.Length >= 16)
							spawnTicks[p.Body.ReadU32Be(12)] = p.Ticks;
						continue;
					}

					if (p.SubOpcode != CombatShieldCodec.Opcode || p.Body.Length < sizeof(uint))
						continue;

					CombatShieldIIR rose;
					try
					{
						rose = CombatShieldCodec.Parse(p.Body.AsSpan(sizeof(uint)));
					}
					catch (Exception)
					{
						continue;
					}

					total++;
					var sinceSpawn = spawnTicks.TryGetValue(rose.Handle, out var spawned)
						? TimeSpan.FromTicks(p.Ticks - spawned).TotalSeconds
						: double.NaN;
					var sincePrevious = previous.TryGetValue(rose.Handle, out var last)
						? TimeSpan.FromTicks(p.Ticks - last.Ticks).TotalSeconds
						: double.NaN;

					var flags = "";
					if (double.IsNaN(sinceSpawn))
						noSpawnSeen++;
					else if (double.IsNaN(sincePrevious) && sinceSpawn <= 2.0)
					{
						afterSpawn++;
						flags += " CATCH-UP?"; // a FIRST rose hard on the heels of the spawn is what a catch-up looks like
					}

					if (!double.IsNaN(sincePrevious) && last.Value == rose.Value)
					{
						unchanged++;
						flags += " UNCHANGED";
					}

					lines.Add($"    0x{rose.Handle:X8}  value={rose.Value:000}  +{Fmt(sincePrevious)}s since previous  +{Fmt(sinceSpawn)}s since spawn{flags}");
					previous[rose.Handle] = (p.Ticks, rose.Value);
				}

				if (lines.Count == 0)
					continue;

				Console.WriteLine($"{capture.Name}  ({lines.Count} rose record(s))");
				foreach (var line in lines)
					Console.WriteLine(line);
			}

			Console.WriteLine();
			Console.WriteLine($"TOTAL over {captures.Count} captures: {total} CombatShieldIIR record(s).");
			Console.WriteLine($"  first rose within 2s of that handle's spawn (a catch-up would look like this): {afterSpawn}");
			Console.WriteLine($"  re-sends of the PREVIOUS value (not a shift):                                  {unchanged}");
			Console.WriteLine($"  no spawn for that handle in the same capture:                                  {noSpawnSeen}");
			return;

			static string Fmt(double seconds)
			{
				return double.IsNaN(seconds) ? "  -  " : $"{seconds,5:0.0}";
			}
		}
	}
}

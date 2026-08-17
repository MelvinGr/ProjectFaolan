using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Constants;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Cli
{
	// `--report corpse-timing`: what retail actually does between a creature dying and the world forgetting it -
	// when the loot bag appears and where, how long the BODY lies there, how long an unlooted bag lasts, and whether
	// the creature ever comes back.
	//
	// Written because all four of our numbers were invented and three of them were the wrong SHAPE, not just the wrong
	// value. We held the bag back 3 s and then despawned the corpse to make room for it; retail drops the bag in the
	// death's own packet burst as a separate dynel and leaves the body lying beside it for another half-minute. And we
	// respawned the creature 15 s after it died - before its own corpse would have finished fading.
	//
	// ## Reading the respawn: the creature comes back on a NEW handle
	//
	// The obvious test - `death(H)` followed by `spawn(H)` - finds NOTHING, and believing it cost a whole cycle.
	// A handle is stable for as long as an entity LIVES (the same handle re-spawns at identical coords across
	// interest churn), but a respawn is a new entity: retail allocates a fresh id for the revived creature. So the
	// question is never what happened to the handle. It is whether the SAME CREATURE reappears AT the dead one's
	// spawn point, and that is what `refills` measures.
	//
	// Three things produce a spawn of a DEAD handle and none of them is a respawn, which is why the same-handle
	// scan is kept as a control rather than deleted:
	//   * the CORPSE being re-streamed as the player walks in and out of range of it - it is still an entity, so it
	//     quits and re-spawns like anything else, and does so while the body is still there (one Cavecrawler three
	//     times before its own fade). Hence `> corpseGone`, anchored on the FADE rather than the first quit.
	//   * the handle being REUSED for a different creature once the corpse is gone - it lands wherever that creature
	//     is, which is what `Moved` exposes.
	//   * a player's own resurrect, which is why player handles are excluded outright.
	//
	// ⚠️ There is no usable "the client watched and nothing came back" bound, and an earlier version of this report
	// printed one. It cannot distinguish "no respawn happened" from "the client was not in range at the moment it
	// did" - `Brutal Pirate 0xaa88` respawned at 150.0 s in the very capture whose witness window claimed 334 s of
	// nothing. A non-observation of a respawn is worth nothing here; only the observed intervals are.
	//
	// Exit code is 0 always - this is a measurement, not a gate. The populations move whenever captures are added
	// (memory: a shipped-data check PRINTS rather than pins).
	public static partial class ReportCli
	{
		// The death record: BehaviorId 0x1F at phase 0x0004 (the same pair `--report death-timing` filters on).
		private const uint CorpseDeathBehaviorId = 0x0000001F;
		private const ushort CorpseDeathSeq = 0x0004;

		// How close a returning creature must be to the dead one's anchor to count as the same spawn point. Retail's
		// revive is exact - the measured clusters all land at 0.0 u - so this is slack for a mob that took a step
		// before its first captured transform, not a search radius.
		private const float RespawnAnchorRadius = 3.0f;


		// The corpse teardown's own length, used only as the fallback marker for "the body is gone" on a death whose
		// fade the dump did not catch. The measured value, which is where WorldEntity.CorpseFadeSeconds comes from.
		private const double WorldCorpseFadeSeconds = 30.8;

		// A bag is this kill's if it spawned within this long of the death. Retail's is 0.00 s; the window is wide
		// enough to catch a late one and narrow enough not to swallow the NEXT kill in a pull.
		private const double BagMatchWindowSeconds = 6.0;

		private static void CorpseTiming(IReadOnlyList<NamedCapture> captures)
		{
			var fades = new List<double>();
			var quits = new List<double>();
			var fadeSecondsByClass = new Dictionary<uint, List<float>>();
			var bagDelays = new List<double>();
			var bagOffsets = new List<Vector3>();
			var bagLives = new List<(double Seconds, bool Faded)>();
			var deaths = 0;
			var respawns = new List<(string Capture, string Name, uint Handle, double Seconds, float Moved)>();
			var churnSpawns = 0;
			var refills = new List<(string Capture, string Dead, uint DeadHandle, string Fill, uint FillHandle, double Seconds, float Dist)>();

			foreach (var c in captures)
			{
				var events = CorpseEvents(c, out var names, out var anchors, out var lastSeen);
				if (events.Count == 0)
					continue;

				foreach (var e in events.Where(e => e.Kind == CorpseEventKind.Spawn))
				{
					// A spawn of a handle that has already been seen alive is interest churn, not a birth - the control
					// that proves the respawn scan below can see a re-spawn at all when there is one to see.
					if (events.Any(p => p.Kind == CorpseEventKind.Spawn && p.Handle == e.Handle && p.Seconds < e.Seconds))
						churnSpawns++;
				}

				// PLAYERS are not creatures: a player's death is followed by their own resurrect/re-entry spawn, which
					// would read as the fastest "respawn" in the corpus if it were counted. NpcSpawnExtractor draws the
					// same line at the same constant, which is why these handles never resolve to a name.
					foreach (var death in events.Where(e => e.Kind == CorpseEventKind.Death && e.Handle < EntityHandles.CharacterInstanceBase))
					{
						deaths++;

					var fade = events.FirstOrDefault(e => e.Kind == CorpseEventKind.Fade && e.Handle == death.Handle && e.Seconds > death.Seconds);
					if (fade != null)
					{
						fades.Add(fade.Seconds - death.Seconds);
						fadeSecondsByClass.TryAdd(fade.ClassWord, []);
						fadeSecondsByClass[fade.ClassWord].Add(fade.FadeSeconds);
					}

					// ⚠️ The corpse's quit is the one AFTER its fade, not merely the first quit after the death. A dead
						// entity is still interest-managed - the player walks away from the body and it quits, walks back
						// and it re-spawns - so the first quit is often that, tens of seconds early. The FADE is the
						// teardown's own marker: it fires once, ~30.8 s in, and the handle is dropped just after it.
						var quit = events.FirstOrDefault(e => e.Kind == CorpseEventKind.Quit && e.Handle == death.Handle && e.Seconds > (fade?.Seconds ?? death.Seconds));
						if (quit != null && fade != null)
							quits.Add(quit.Seconds - death.Seconds);

					// Did this creature come back? A spawn of a dead handle is NOT enough on its own - two other things
					// produce one, and both are common:
					//   * the CORPSE being re-streamed as the player walks in and out of range of it. It is still an
					//     entity, so it quits and re-spawns like anything else, and it does so while the body is still
					//     there - one Cavecrawler does it three times before its own fade. Hence `> corpseGone`.
					//   * the handle being REUSED for a different creature once the corpse is gone. That one lands
					//     wherever the new creature is, so it fails `Moved` - a genuine respawn is AT the spawn point,
					//     which is where retail's revive puts it (and where every captured re-spawn of a live NPC is).
					// Neither is filtered away silently: `Moved` is printed for every candidate, so a wrong call here
					// is visible rather than assumed.
					var corpseGone = quit?.Seconds ?? fade?.Seconds ?? death.Seconds + WorldCorpseFadeSeconds;
					var back = events.FirstOrDefault(e => e.Kind == CorpseEventKind.Spawn && e.Handle == death.Handle && e.Seconds > corpseGone + 5.0);
					if (back != null)
						respawns.Add((c.Name, names.GetValueOrDefault(death.Handle, "?"), death.Handle, back.Seconds - death.Seconds,
							anchors.TryGetValue(death.Handle, out var home0) ? Ground(back.Position - home0) : float.NaN));
					// ⭐ THE respawn signal, and the one the same-handle test above cannot see. A revived creature comes
					// back on a NEW entity id, so the question is not what happened to the handle - it is whether the
					// SAME CREATURE reappears AT the dead one's spawn point. Same name, within a few units of its
					// anchor; the distance rides along and is printed, so a coincidental neighbour (two mobs of one camp
					// standing close) can be told apart from a genuine refill by eye rather than by trusting the radius.
					if (anchors.TryGetValue(death.Handle, out var anchor) && names.TryGetValue(death.Handle, out var deadName))
					{
						var fill = events.FirstOrDefault(e => e.Kind == CorpseEventKind.Spawn && e.Handle != death.Handle && e.Seconds > death.Seconds
						                                     && names.GetValueOrDefault(e.Handle) == deadName && Ground(e.Position - anchor) <= RespawnAnchorRadius);
						if (fill != null)
							refills.Add((c.Name, deadName, death.Handle, deadName, fill.Handle, fill.Seconds - death.Seconds, Ground(fill.Position - anchor)));
					}

					// The bag this kill dropped: the nearest un-taken one inside the match window.
					var bag = events.FirstOrDefault(e => e.Kind == CorpseEventKind.BagSpawn && !e.Taken && e.Seconds >= death.Seconds - 0.5 && e.Seconds - death.Seconds <= BagMatchWindowSeconds);
					if (bag == null)
						continue;

					bag.Taken = true;
					bagDelays.Add(bag.Seconds - death.Seconds);
					if (anchors.ContainsKey(death.Handle))
						bagOffsets.Add(bag.Position - LastKnown(events, death.Handle, death.Seconds));

					var gone = events.FirstOrDefault(e => e.Kind is CorpseEventKind.Fade or CorpseEventKind.Quit && e.Handle == bag.Handle && e.Seconds > bag.Seconds);
					if (gone != null)
						bagLives.Add((gone.Seconds - bag.Seconds, gone.Kind == CorpseEventKind.Fade));
				}
			}

			Console.WriteLine($"=== corpse timing over {captures.Count} capture(s): {deaths} NPC death(s) ===");
			Console.WriteLine();
			Stat("corpse: death -> n3ToClientFade", fades);
			Stat("corpse: death -> n3ToClientQuit", quits);
			Console.WriteLine();

			Console.WriteLine("the fade's own `fadeSeconds` float, by entity class (invariant per class is the claim):");
			foreach (var (classWord, values) in fadeSecondsByClass.OrderByDescending(kv => kv.Value.Count))
			{
				var distinct = values.Distinct().OrderBy(v => v).ToList();
				Console.WriteLine($"  0x{classWord:X4} {EntityClassName(classWord),-12} n={values.Count,3}  value(s): {string.Join(", ", distinct.Select(v => v.ToString("0.0#")))}");
			}

			Console.WriteLine();
			Stat("loot bag: death -> bag spawn (0 = the death's own burst)", bagDelays);
			if (bagOffsets.Count > 0)
			{
				var modal = bagOffsets.GroupBy(o => (X: MathF.Round(o.X, 1), Z: MathF.Round(o.Z, 1))).OrderByDescending(g => g.Count()).First();
				Console.WriteLine($"loot bag: offset from the corpse - modal (dx, dz) = ({modal.Key.X:0.0}, {modal.Key.Z:0.0}) on {modal.Count()} of {bagOffsets.Count} kills");
			}

			Stat("loot bag: spawn -> its own fade (looted-empty AND expired)", bagLives.Select(b => b.Seconds).ToList());
			var expired = bagLives.Where(b => b.Seconds > 60).Select(b => b.Seconds).ToList();
			if (expired.Count > 0)
				Console.WriteLine($"  ...of which the UNLOOTED tail (>60 s, i.e. the expiry timer rather than a loot): n={expired.Count} {string.Join(", ", expired.Select(v => v.ToString("0.0")))}");

			Console.WriteLine();
			Console.WriteLine("=== respawn ===");
			Console.WriteLine($"deaths whose handle spawned again after its corpse cleared: {respawns.Count} of {deaths}");
			Console.WriteLine("  (a respawn is AT the spawn point - a candidate far from home is the handle being reused)");
			foreach (var r in respawns.OrderBy(r => r.Seconds))
				Console.WriteLine($"    +{r.Seconds,7:0.0}s  {r.Moved,6:0.0}u from home  {r.Name} 0x{r.Handle:x}  {r.Capture}");

			Console.WriteLine($"RESPAWNS - the same creature back at the dead one's spawn point, on a NEW handle: {refills.Count} of {deaths}");
			foreach (var r in refills.OrderBy(r => r.Seconds))
				Console.WriteLine($"    +{r.Seconds,7:0.1}s  {r.Dist,4:0.0}u  {r.Dead} 0x{r.DeadHandle:x} -> 0x{r.FillHandle:x}  {r.Capture[..System.Math.Min(46, r.Capture.Length)]}");

			Console.WriteLine();
			Console.WriteLine("per creature (one whose observations agree ACROSS captures is a real timer, not a coincidence):");
			foreach (var g in refills.GroupBy(r => r.Dead).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
			{
				var v = g.Select(r => r.Seconds).OrderBy(x => x).ToList();
				var caps = g.Select(r => r.Capture).Distinct().Count();
				Console.WriteLine($"  {g.Key,-24} n={v.Count,2} in {caps} capture(s): {string.Join(", ", v.Select(x => x.ToString("0.0")))}");
			}
			Console.WriteLine($"(control) spawns the same scan calls interest churn: {churnSpawns}");
		}

		private static float Ground(Vector3 delta)
		{
			return MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z); // the ground plane - Y is terrain, not separation
		}

		private static string EntityClassName(uint classWord)
		{
			return classWord switch
			{
				EntityClass.Character => "Character",
				EntityClass.LootBag => "LootBag",
				_ => "",
			};
		}

		private static void Stat(string label, IReadOnlyList<double> values)
		{
			if (values.Count == 0)
			{
				Console.WriteLine($"{label,-58} (none)");
				return;
			}

			var sorted = values.OrderBy(v => v).ToList();
			Console.WriteLine($"{label,-58} n={sorted.Count,3} min={sorted[0],7:0.00} median={sorted[sorted.Count / 2],7:0.00} mean={sorted.Average(),7:0.00} max={sorted[^1],7:0.00}");
		}

		// The last position this handle was seen at before `before` - its spawn, or wherever it stopped since.
		private static Vector3 LastKnown(List<CorpseEvent> events, uint handle, double before)
		{
			return events.Where(e => e.Handle == handle && e.Seconds <= before && e.Kind is CorpseEventKind.Spawn or CorpseEventKind.Stop)
				.OrderBy(e => e.Seconds)
				.Select(e => e.Position)
				.LastOrDefault();
		}

		// Everything one capture says about creatures arriving, standing, dying and leaving, on one timeline. Also
		// yields each handle's HOME anchor (its first spawn position, which is what a respawn would refill) and the
		// last moment the client heard anything at all about it.
		private static List<CorpseEvent> CorpseEvents(NamedCapture c, out Dictionary<uint, string> names, out Dictionary<uint, Vector3> anchors, out Dictionary<uint, double> lastSeen)
		{
			names = [];
			anchors = [];
			lastSeen = [];
			var events = new List<CorpseEvent>();
			var start = c.Packets.Count > 0 ? c.Packets.Min(p => p.Ticks) : 0L;

			foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
			{
				if (!string.IsNullOrEmpty(e.Name))
					names[e.Handle] = e.Name!;
			}

			foreach (var p in c.Packets)
			{
				if (!p.IsIirCarrier || !p.IsDownload)
					continue;

				var seconds = (p.Ticks - start) / (double)TimeSpan.TicksPerSecond;

				switch (p.SubOpcode)
				{
					case BehaviorControlCenterCodec.Opcode:
					{
						if (!p.TryParse(BehaviorControlCenterCodec.Parse, out var bcc) || bcc.Seq != CorpseDeathSeq || bcc.BehaviorId != CorpseDeathBehaviorId)
							break;

						events.Add(new CorpseEvent(seconds, CorpseEventKind.Death, bcc.Actor));
						lastSeen[bcc.Actor] = seconds;
						break;
					}

					case N3ToClientFadeCodec.Opcode:
					{
						if (!p.TryParse(N3ToClientFadeCodec.Parse, out var fade))
							break;

						events.Add(new CorpseEvent(seconds, CorpseEventKind.Fade, fade.Handle) { ClassWord = fade.TypeTag, FadeSeconds = fade.FadeSeconds });
						break;
					}

					case N3ToClientQuitCodec.Opcode:
					{
						if (!p.TryParse(N3ToClientQuitCodec.Parse, out var quit))
							break;

						events.Add(new CorpseEvent(seconds, CorpseEventKind.Quit, quit.Handle));
						break;
					}

					case StopCodec.Opcode:
					{
						if (!p.TryParse(StopCodec.Parse, out var stop))
							break;

						events.Add(new CorpseEvent(seconds, CorpseEventKind.Stop, stop.Handle) { Position = stop.Position });
						lastSeen[stop.Handle] = seconds;
						break;
					}

					case SimpleCharFullUpdateCodec.Opcode:
					case WorldObjectClasses.LootBag:
					{
						// ⚠️ `Body`, not `Record`: SpawnPacketBuilder's offsets are relative to `[u32 len][u32 subop]
						// [u32 class][u32 handle]...`, and `Record` has already eaten that leading length word. Reading
						// them off `Record` silently yields a handle four bytes into the wrong field - which parses, and
						// gives every spawn a plausible-looking wrong id.
						var body = p.Body;
						if (body.Length < SpawnPacketBuilder.PosZOffset + 4)
							break;

						var handle = body.ReadU32Be(SpawnPacketBuilder.HandleOffset);
						var position = new Vector3(body.ReadF32Le(SpawnPacketBuilder.PosXOffset), body.ReadF32Le(SpawnPacketBuilder.PosYOffset), body.ReadF32Le(SpawnPacketBuilder.PosZOffset));
						var kind = p.SubOpcode == WorldObjectClasses.LootBag ? CorpseEventKind.BagSpawn : CorpseEventKind.Spawn;
						events.Add(new CorpseEvent(seconds, kind, handle) { Position = position });
						if (kind == CorpseEventKind.Spawn)
							anchors.TryAdd(handle, position);

						lastSeen[handle] = seconds;
						break;
					}
				}
			}

			foreach (var e in events)
				lastSeen[e.Handle] = Math.Max(lastSeen.GetValueOrDefault(e.Handle), e.Seconds);

			return events;
		}

		private enum CorpseEventKind
		{
			Spawn,
			Stop,
			Death,
			Fade,
			Quit,
			BagSpawn,
		}

		private sealed class CorpseEvent(double seconds, CorpseEventKind kind, uint handle)
		{
			public double Seconds { get; } = seconds;
			public CorpseEventKind Kind { get; } = kind;
			public uint Handle { get; } = handle;
			public Vector3 Position { get; init; }
			public uint ClassWord { get; init; }
			public float FadeSeconds { get; init; }

			// Set once a bag has been attributed to a kill, so a pull of eight mobs cannot match all eight to one bag.
			public bool Taken { get; set; }
		}
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Extractors
{
	// Which near-identical STATIONARY spawns are one placement seen twice (#718 / #765), decided from the corpus rather
	// than from a distance.
	//
	// #713 solved the PATROLLER case: a harvested beat identifies the placement, and rows sharing one are that
	// placement's variant pool. A stationary NPC has no beat. Its position is nearly - but not exactly - stable across
	// sessions, so the importer's (MapId, XYZ) key mints a second placement whenever a sighting lands a few
	// centimetres off, and 344 same-map/same-template pairs sit within 5u of each other.
	//
	// ⚠️ Neither proximity NOR "identical in every other column" can separate the two populations. Retail really does
	// place same-name NPCs a metre apart - Black Dragon Guards flanking a Tarantia doorway, a pack of Enraged Boars -
	// and those rows are identical in every column by construction, being the same creature at the same level in the
	// same kit. Measured: of 191 candidate clusters, **181 are real** concurrent placements. A distance rule would have
	// collapsed all of them, which is the mistake the old name/cell heuristic made.
	//
	// **The HANDLE decides it.** Two real placements are two entities and retail mints them two handles; one NPC
	// harvested twice is ONE entity at two moments. So the positive test is: was a single handle ever sighted at two of
	// the cluster's positions? If it was, that entity WALKED between them and they are one placement. This is the
	// stationary analogue of #713's route-occupancy check, and like it, it is a fact about the corpus rather than a
	// tolerance.
	//
	// ⚠️ "The same CAPTURE saw two of these positions" is NOT the test, and getting that wrong inverts the answer: a
	// creature that moves is re-streamed by interest within one session, so one capture holding both sightings is
	// precisely the duplicate case seen from the other side. Read that way, the corpus calls the Scruffy trio of #765
	// two real placements; read by handle, it says one handle stood on two of them in two separate captures.
	public static class StationaryPlacementResolver
	{
		// How close a captured spawn must sit to a row's stored position to count as a sighting OF that row. Only has to
		// beat the gaps WITHIN a cluster, and those are metres.
		public const float SightingTolerance = 0.75f;

		// What makes two stationary rows candidates for being one placement. 5u, matching the #718 census.
		public const float ClusterRadius = 5f;

		// Why a cluster was or was not collapsed. Reported as-is - the two negative verdicts are different evidence and
		// must not be merged: `Concurrent` is a fact about retail, `NeverSighted` is a hole in the corpus.
		public enum Verdict
		{
			OneWalked, // a single handle stood on two members: one placement, collapse
			Concurrent, // two handles at once: two real placements, leave alone
			SightedApart, // sighted, but never one handle across two members and never two at once - no evidence either way
			NeverSighted // no capture put a spawn at any of these positions - the corpus cannot answer
		}

		public sealed record Cluster(IReadOnlyList<NpcSpawn> Rows, Verdict Verdict, int Evidence, int CapturesSeeingOne)
		{
			// The key every row of a COLLAPSED cluster carries, so the world stands ONE entity up and draws its
			// appearance from the pool (the runtime half is already built - see WorldBroadcaster and NpcSpawn.PlacementId).
			//
			// Derived, never generated: FNV-1a over the template and the cluster's LOWEST spawn id. That id is itself
			// derived from (map, position, class), so it is stable across re-imports and across machines - which
			// `string.GetHashCode` explicitly is not, and this value is persisted.
			public uint PlacementKey()
			{
				var hash = 2166136261u;
				Mix(ref hash, Rows[0].NpcTemplateId);
				Mix(ref hash, Rows.Min(r => r.Id));
				return hash;

				static void Mix(ref uint hash, uint value)
				{
					for (var i = 0; i < 4; i++)
						hash = unchecked((hash ^ (byte)(value >> (i * 8))) * 16777619u);
				}
			}
		}

		// Every candidate cluster with its verdict. `rows` is the whole spawn set; `routed` the spawn ids that carry a
		// harvested beat (those are #713's case and are skipped).
		public static List<Cluster> Resolve(IReadOnlyList<NpcSpawn> rows, ISet<uint> routed, IReadOnlyList<NamedCapture> captures)
		{
			ArgumentNullException.ThrowIfNull(rows);
			ArgumentNullException.ThrowIfNull(routed);
			ArgumentNullException.ThrowIfNull(captures);

			// (map, template) -> every captured sighting, carrying the HANDLE it arrived on.
			var sightings = new Dictionary<(uint Map, uint Template), List<(string Capture, uint Handle, Vector3 At)>>();
			foreach (var c in captures)
			{
				foreach (var e in NpcSpawnExtractor.Extract(c.Packets))
				{
					if (e.TemplateId == 0)
						continue;

					if (!sightings.TryGetValue((e.MapId, e.TemplateId), out var list))
						sightings[(e.MapId, e.TemplateId)] = list = [];

					list.Add((c.Name, e.Handle, new Vector3(e.X, e.Y, e.Z)));
				}
			}

			var clusters = new List<Cluster>();
			foreach (var cluster in Candidates(rows, routed))
				clusters.Add(Judge(cluster, sightings.GetValueOrDefault((cluster[0].MapId, cluster[0].NpcTemplateId), [])));

			return clusters;
		}

		// Same map + same template + same CLASS, no harvested beat, single-linked within ClusterRadius.
		//
		// ⚠️ ClassId is part of the grouping, not a detail. On a class-specific playfield (the city armories) each
		// class's copy has its own population, so rows can sit at the SAME point legitimately - `Quartermaster Haji` on
		// map 5007 is three rows at spread 0.00u, one per captured class. Grouping those would report the tidiest
		// imaginable duplicate and be entirely wrong.
		private static List<List<NpcSpawn>> Candidates(IReadOnlyList<NpcSpawn> rows, ISet<uint> routed)
		{
			var clusters = new List<List<NpcSpawn>>();
			foreach (var g in rows.Where(r => !routed.Contains(r.Id)).GroupBy(r => (r.MapId, r.NpcTemplateId, r.ClassId)))
			{
				var pending = g.ToList();
				while (pending.Count > 0)
				{
					var cluster = new List<NpcSpawn> { pending[^1] };
					pending.RemoveAt(pending.Count - 1);
					for (var grew = true; grew;)
					{
						grew = false;
						for (var i = pending.Count - 1; i >= 0; i--)
						{
							if (!cluster.Any(c => Vector3.Distance(c.Position, pending[i].Position) <= ClusterRadius))
								continue;

							cluster.Add(pending[i]);
							pending.RemoveAt(i);
							grew = true;
						}
					}

					if (cluster.Count > 1)
						clusters.Add(cluster);
				}
			}

			return clusters;
		}

		private static Cluster Judge(List<NpcSpawn> cluster, List<(string Capture, uint Handle, Vector3 At)> seen)
		{
			// Which cluster members each (capture, handle) - i.e. each ENTITY-in-a-session - was seen standing at.
			var perEntity = new Dictionary<(string Capture, uint Handle), HashSet<uint>>();
			var perCapture = new Dictionary<string, HashSet<uint>>();
			foreach (var (capture, handle, at) in seen)
			{
				foreach (var row in cluster.Where(r => Vector3.Distance(r.Position, at) <= SightingTolerance))
				{
					if (!perEntity.TryGetValue((capture, handle), out var byEntity))
						perEntity[(capture, handle)] = byEntity = [];

					byEntity.Add(row.Id);

					if (!perCapture.TryGetValue(capture, out var members))
						perCapture[capture] = members = [];

					members.Add(row.Id);
				}
			}

			var walked = perEntity.Count(e => e.Value.Count > 1);
			if (walked > 0)
				// Order by id so PlacementKey and the reported pool are deterministic.
				return new Cluster(cluster.OrderBy(r => r.Id).ToList(), Verdict.OneWalked, walked, perCapture.Count);

			// Two DIFFERENT entities on two DIFFERENT members inside one capture: both were in the world at once.
			var concurrent = perCapture.Count(c =>
			{
				var entities = perEntity.Where(e => e.Key.Capture == c.Key).ToList();
				return entities.Select(e => e.Key.Handle).Distinct().Count() > 1 && entities.SelectMany(e => e.Value).Distinct().Count() > 1;
			});

			var verdict = perCapture.Count == 0
				? Verdict.NeverSighted
				: concurrent > 0
					? Verdict.Concurrent
					: Verdict.SightedApart;

			return new Cluster(cluster.OrderBy(r => r.Id).ToList(), verdict, concurrent, perCapture.Count);
		}
	}
}

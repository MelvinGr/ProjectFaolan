using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb;
using Faolan.Core.Rdb.Assets;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Pre-fills each NpcSpawnArea's creature (NpcTemplateId) from what we CAN infer, since the RDB carries WHERE + how
	// many but never WHICH (#363). Four confidence tiers, applied in order, never overwriting a Manual assignment:
	//   1. Name         - the group name / Name-id (4-char tag stripped) matches a spawnable NpcTemplate by name.
	//   2. Containment  - unanimous (>= 2) captured NpcSpawns standing ON the group's RDB wander path; the whole roam
//                     AABB is only a fallback, gated by the same name guard as tier 3 (#491). Captured maps only.
	//   3. IdPropagation- an id->creature learned from tiers 1-2, propagated to a same-id group, doubly GUARDED:
	//                     the group's own name must not contradict the id's creature (Slaver1 under Poacher's
	//                     Community 60 is left alone), AND the id must have been resolved by tier 1-2 on THAT MAP
	//                     (#489) - a numeric id names a family, not a species, so an observation on one playfield
	//                     says nothing about the same family on another.
	//   4. RaceUnique   - the group's own DECLARED race (a `Race`-category id IS the RDB 1000508 race id), where we
	//                     hold exactly ONE spawnable template of that race. The client states the race, so this is
	//                     the only tier that reads rather than infers; the unambiguity gate is what keeps it from
	//                     becoming a guess, and a race with several candidates is left unresolved (#518).
	// Machine assignments are RESET at the start, so this is idempotent and a rules change reaches existing rows;
	// a Manual assignment is never touched. The rest await captures / a manual `.setnpcgroup` pass - honestly
	// unknown rather than wrongly guessed.
	public static class SpawnAreaResolver
	{
		private const float ContainPad = 10f; // captured-spawn search: pad the roam AABB by this (world units), X/Z
		// The 1010506 group `category` whose id is a creature race (RDB 1000508). Named once - tier 4 keys on it.
		private const string RaceCategory = "Race";

		private static readonly Regex FourCharTag = new Regex(@"\s*\([0-9A-Za-z]{4}\)\s*$", RegexOptions.Compiled);

		// Name families that carry no creature claim - a group so named may take a propagated creature even if its name
		// doesn't match (it's a location/role label, not a species). Keeps the guard from blocking legitimate propagation.
		private static readonly Regex TerritoryLabel = new Regex(
			@"territory|area|mound|burial|captives|boss|library|treasury|infirmary|scout|party|trash|point|patrol|roaming|citizen|spawngroup|zone|region|wave|node|camp", RegexOptions.Compiled);

		private static readonly Regex TrailingIndex = new Regex(@"[\s_\-]*\d+\s*$", RegexOptions.Compiled);
		private static readonly Regex Word = new Regex(@"[a-z]+", RegexOptions.Compiled);

		// The client's creature race table (RDB 1000508), or null when the RDB has no such entry. Tier 4's guard needs
		// the race NAMES, not just the ids, so both callers load it here rather than each rolling their own.
		public static CreatureRaces? LoadRaceNames(RdbReader rdb)
		{
			return rdb.TryGet(CreatureRaceTable.RdbType, 0, out var entry) && rdb.CanRead(entry)
				? CreatureRaceTable.Parse(rdb.ReadDecompressed(entry))
				: null;
		}

		// `roamPaths` maps an NpcSpawnArea id to that territory's RDB wander polylines (1010506 `RoamPaths`). It is the
		// containment tier's membership test (#491) and is NOT stored in the DB - ~740k points corpus-wide would dwarf
		// the seed - so both callers read it from the RDB and hand it in. Pass null and tier 2 falls back to the roam
		// AABB alone, which is the pre-#491 behaviour.
		public static async Task<ResolveStats> ResolveAsync(DatabaseContext db, IReadOnlyDictionary<uint, IReadOnlyList<IReadOnlyList<Vector3>>>? roamPaths = null,
			CreatureRaces? raceNames = null)
		{
			// Spawnable templates (those with a StatCore) keyed by lowercase name -> a representative TemplateId.
			var withStatCore = (await db.NpcStatCores.Select(s => s.NpcTemplateId).ToListAsync()).ToHashSet();
			var templateByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
			var nameByTemplate = new Dictionary<uint, string>();
			foreach (var t in await db
				         .NpcTemplates.Where(t => t.Name != null)
				         .Select(t => new
				         {
					         t.TemplateId,
					         t.Name
				         })
				         .ToListAsync())
			{
				nameByTemplate[t.TemplateId] = t.Name!;
				if (withStatCore.Contains(t.TemplateId))
					templateByName.TryAdd(t.Name!.ToLowerInvariant(), t.TemplateId);
			}

			// Captured placements per map (name + X/Z), for the containment tier.
			var capturedByMap = new Dictionary<uint, List<(uint TemplateId, string Name, float X, float Z)>>();
			foreach (var s in await db
				         .NpcSpawns.Select(s => new
				         {
					         s.MapId,
					         s.NpcTemplateId,
					         s.PositionX,
					         s.PositionZ
				         })
				         .ToListAsync())
			{
				if (!nameByTemplate.TryGetValue(s.NpcTemplateId, out var nm))
					continue;

				if (!capturedByMap.TryGetValue(s.MapId, out var list))
					capturedByMap[s.MapId] = list = [];
				list.Add((s.NpcTemplateId, nm, s.PositionX, s.PositionZ));
			}

			var areas = await db.NpcSpawnAreas.Include(a => a.Points).ToListAsync();

			var name = 0;
			var contain = 0;
			var idprop = 0;

			// ---- Reset every MACHINE assignment first (#489) ----
			// Without this the resolver is not idempotent and a rule change cannot take effect: tier 3 only ever
			// touches areas still at None, so an area already carrying a (now-disallowed) IdPropagation guess would
			// simply be skipped and keep it forever. Manual is a human's answer and is never touched.
			foreach (var a in areas)
			{
				if (a.ResolvedBy == SpawnResolutionSource.Manual)
					continue;

				a.NpcTemplateId = null;
				a.ResolvedBy = SpawnResolutionSource.None;
			}

			// ---- Tier 1: name ----
			foreach (var a in areas)
			{
				if (a.ResolvedBy == SpawnResolutionSource.Manual)
					continue;

				var t = NameResolve(a, templateByName);
				if (t != null)
				{
					a.NpcTemplateId = t;
					a.ResolvedBy = SpawnResolutionSource.Name;
					name++;
				}
			}

			// ---- Tier 2: gated containment (unanimous, >= 2 votes), name-unresolved areas only ----
			// The roam AABB is a crude membership test: it also contains the hand-placed NPCs standing inside it, and
			// their votes break unanimity or, worse, win it - Khopshef's "Scorpion" groups resolved to Flesheater and
			// Conall's "Rabbits" to Warg. A group's actual members roam ALONG its RDB path polyline, so a spawn
			// standing on that path is evidence and one merely inside the box is not (#491).
			//
			// So: vote on the ON-PATH spawns first, and fall back to the whole box only when that says nothing - and
			// then only if the group's own NAME does not contradict the creature, the same guard tier 3 uses. The
			// fallback is what keeps groups whose members were caught mid-stride between path samples (Tortage's
			// Ape2/Ape3) while still rejecting Scorpion -> Flesheater. Measured on the corpus: 16 resolutions with 4
			// name-contradicting -> 18 with 1 (Elk -> Moose, which is the guard being over-strict about a synonym).
			foreach (var a in areas)
			{
				if (a.ResolvedBy is SpawnResolutionSource.Name or SpawnResolutionSource.Manual)
					continue;
				if (!capturedByMap.TryGetValue(a.MapId, out var caps))
					continue;

				var (x0, x1, z0, z1) = ContainmentRegion(a);
				var inside = caps.Where(c => c.X >= x0 && c.X <= x1 && c.Z >= z0 && c.Z <= z1).ToList();
				if (inside.Count < 2)
					continue;

				var segments = roamPaths?.GetValueOrDefault(a.Id);
				var chosen = segments is { Count: > 0 }
					? UnanimousTemplate(inside.Where(c => OnRoamPath(c.X, c.Z, segments)).ToList())
					: null;

				if (chosen == null)
				{
					var fallback = UnanimousTemplate(inside);
					var fam = Family(a.GroupName);
					if (fallback != null && (NameAgrees(fam, nameByTemplate.GetValueOrDefault(fallback.Value, ""), []) || TerritoryLabel.IsMatch(fam) || fam.Length == 0))
						chosen = fallback;
				}

				if (chosen == null)
					continue;

				a.NpcTemplateId = chosen;
				a.ResolvedBy = SpawnResolutionSource.Containment;
				contain++;
			}

			// ---- Tier 3: guarded id-propagation ----
			// Learn (category, id) -> creature + the defining name-families, from tier-1/2 resolutions.
			var idVotes = new Dictionary<(string, uint), Dictionary<uint, int>>();
			var idDefiningFamilies = new Dictionary<(string, uint), HashSet<string>>();
			// Which MAPS a key was actually observed on (#489). A numeric id names a family, not a species, so an
			// observation on one playfield says nothing about the same family on another - Poitain's 29 groups all
			// became Panther off a (Superfamily, 4) key whose only captured panthers are in Tortage.
			var idEvidenceMaps = new HashSet<(string, uint, uint)>();
			foreach (var a in areas)
			{
				if (a.ResolvedBy is not (SpawnResolutionSource.Name or SpawnResolutionSource.Containment) || a.NpcTemplateId is not { } tid)
					continue;
				if (!IsNumericIdCategory(a.Category) || a.CommunityId is not { } cid)
					continue;

				var key = (a.Category, cid);
				if (!idVotes.TryGetValue(key, out var votes))
					idVotes[key] = votes = [];
				votes[tid] = votes.GetValueOrDefault(tid) + 1;
				if (!idDefiningFamilies.TryGetValue(key, out var fams))
					idDefiningFamilies[key] = fams = [];
				fams.Add(Family(a.GroupName));
				idEvidenceMaps.Add((a.Category, cid, a.MapId));
			}

			var idMap = new Dictionary<(string, uint), uint>();
			foreach (var (key, votes) in idVotes)
			{
				var top = votes.OrderByDescending(kv => kv.Value).First();
				if (votes.Count == 1 || top.Value / (double)votes.Values.Sum() >= 0.8)
					idMap[key] = top.Key;
			}

			foreach (var a in areas)
			{
				if (a.ResolvedBy != SpawnResolutionSource.None)
					continue;
				if (!IsNumericIdCategory(a.Category) || a.CommunityId is not { } cid)
					continue;

				var key = (a.Category, cid);
				if (!idMap.TryGetValue(key, out var creatureTid))
					continue;

				// SAME-MAP EVIDENCE (#489): the key must have been resolved by name/containment on THIS playfield.
				// Without it, 319 of 329 propagations (3697 NPCs across 17 maps) rested on an observation made
				// somewhere else entirely. An area with no local evidence stays None - honestly unknown, and
				// `.setnpcgroup` remains the way to answer it.
				if (!idEvidenceMaps.Contains((a.Category, cid, a.MapId)))
					continue;

				var fam = Family(a.GroupName);
				var creatureName = nameByTemplate.GetValueOrDefault(creatureTid, "");
				var defs = idDefiningFamilies.GetValueOrDefault(key, []);

				// GUARD: propagate only when the name AGREES with the id's creature, or the name is a
				// location/role label with no creature claim. A conflicting creature-name (Slaver vs Poacher) blocks it.
				if (NameAgrees(fam, creatureName, defs) || TerritoryLabel.IsMatch(fam) || fam.Length == 0)
				{
					a.NpcTemplateId = creatureTid;
					a.ResolvedBy = SpawnResolutionSource.IdPropagation;
					idprop++;
				}
			}

			// ---- Tier 4: the group's own DECLARED race, where that race is UNAMBIGUOUS (#518) ----
			// A `Race`-category group carries the creature's race id (RDB 1000508) as its `CommunityId`. That is the
			// client STATING what lives here, not us inferring it from a name or a geometry vote - so where we hold
			// exactly ONE spawnable template of that race, there is nothing left to choose and the area resolves.
			//
			// The unambiguity gate is the whole point, and it is what keeps this off the guess #518 argues against.
			// Of the unresolved Race groups: 284 areas sit on a race with exactly one candidate template, 36 on a
			// race with 2-3, 555 on a race with 4 or more, and 260 on a race we hold no template for at all. Only the
			// first bucket is taken. Standing an ARBITRARY one of eleven Undead templates into a camp would be the
			// wrong-guess-is-worse-than-absent trade; standing our one rabbit into a `GrasslandsRabbit` group is not.
			//
			// Runs LAST so it never displaces a name/containment/propagation answer, and it carries the same
			// name-contradiction guard tier 3 uses - a group whose own name names a different creature family is
			// left alone rather than overridden by its race.
			var raceCandidates = new Dictionary<uint, List<uint>>();
			foreach (var s in await db
				         .NpcStatCores.Where(s => s.Race != null && s.Race != 0)
				         .Select(s => new
				         {
					         s.NpcTemplateId,
					         Race = s.Race!.Value
				         })
				         .ToListAsync())
			{
				if (!raceCandidates.TryGetValue(s.Race, out var list))
					raceCandidates[s.Race] = list = [];
				list.Add(s.NpcTemplateId);
			}

			var raceUnique = 0;
			foreach (var a in areas)
			{
				if (a.ResolvedBy != SpawnResolutionSource.None || a.Category != RaceCategory || a.CommunityId is not { } raceId)
					continue;

				// Exactly one candidate, or nothing doing. `Distinct()` because a race's templates are collected per
				// StatCore row and the same template must not look like two candidates.
				if (!raceCandidates.TryGetValue(raceId, out var candidates))
					continue;

				var distinct = candidates.Distinct().ToList();
				if (distinct.Count != 1)
					continue;

				// GUARD on the RACE name, not the template name. Tier 3's guard compares the group name to the
				// creature's name because there a numeric id names a family and the name is the only cross-check.
				// Here the race itself is named (RDB 1000508), so the sharper question is available: does the group's
				// own name claim a DIFFERENT race than the one it declares? `GrasslandsRabbit42` declares race 72 and
				// names Rabbit - agreement, even though the template is called `Old Buck`, which the template-name
				// guard would have rejected. A name claiming no race at all (`Territory14`) passes: it contradicts
				// nothing. Only an actual conflict blocks.
				if (raceNames?.ContradictsRace(a.GroupName, raceId) == true)
					continue;

				a.NpcTemplateId = distinct[0];
				a.ResolvedBy = SpawnResolutionSource.RaceUnique;
				raceUnique++;
			}

			await db.SaveChangesAsync();

			// #363 auto-link: materialize which captured NpcSpawns each resolved area OWNS (same creature, inside the
			// bounds), so graduation is a clean FK delete instead of a re-derived bounds match. Runs on the maps that
			// have resolved areas (the captured ones); uncaptured maps have no spawns to link.
			var linked = await LinkCapturedSpawns(db, areas);

			return new ResolveStats(name, contain, idprop, raceUnique, name + contain + idprop + raceUnique, areas.Count, linked);
		}

		// The one template every candidate agrees on (>= 2 of them), or null when the set is short or split.
		private static uint? UnanimousTemplate(List<(uint TemplateId, string Name, float X, float Z)> candidates)
		{
			if (candidates.Count < 2)
				return null;

			var first = candidates[0].TemplateId;
			return candidates.All(c => c.TemplateId == first)
				? first
				: null;
		}

		// Is this spawn standing ON one of the group's wander polylines? Distance to the nearest SEGMENT, not to the
		// nearest sample point - consecutive samples can be metres apart, and a mob between two of them is still on the
		// path. The radius is measured: 59-81% of captured spawns inside a territory sit within 1 world unit of a
		// segment, and the tail beyond it is overwhelmingly hand-placed NPCs rather than members.
		private const float OnPathRadius = 1f;

		private static bool OnRoamPath(float x, float z, IReadOnlyList<IReadOnlyList<Vector3>> paths)
		{
			foreach (var path in paths)
				for (var i = 0; i + 1 < path.Count; i++)
					if (SegmentDistance(x, z, path[i], path[i + 1]) <= OnPathRadius)
						return true;

			return false;
		}

		private static float SegmentDistance(float px, float pz, Vector3 a, Vector3 b)
		{
			float dx = b.X - a.X, dz = b.Z - a.Z;
			var len = dx * dx + dz * dz;
			var t = len <= 0f
				? 0f
				: Math.Clamp(((px - a.X) * dx + (pz - a.Z) * dz) / len, 0f, 1f);
			return MathF.Sqrt(MathF.Pow(px - (a.X + t * dx), 2) + MathF.Pow(pz - (a.Z + t * dz), 2));
		}

		private static (float X0, float X1, float Z0, float Z1) ContainmentRegion(NpcSpawnArea a)
		{
			// The roam AABB, extended to also cover the primary anchor points (which can sit just outside it), padded.
			var x0 = a.BoundsMinX;
			var x1 = a.BoundsMaxX;
			var z0 = a.BoundsMinZ;
			var z1 = a.BoundsMaxZ;
			foreach (var p in a.Points)
			{
				x0 = Math.Min(x0, p.PositionX);
				x1 = Math.Max(x1, p.PositionX);
				z0 = Math.Min(z0, p.PositionZ);
				z1 = Math.Max(z1, p.PositionZ);
			}

			return (x0 - ContainPad, x1 + ContainPad, z0 - ContainPad, z1 + ContainPad);
		}

		// The creature-ish name family of a group: lowercased, trailing index removed, a leading "t_" prefix dropped.
		private static string Family(string? groupName)
		{
			var s = TrailingIndex.Replace(groupName ?? "", "").Trim().ToLowerInvariant();
			return s.StartsWith("t_", StringComparison.Ordinal)
				? s[2..]
				: s;
		}

		private static bool IsNumericIdCategory(string category)
		{
			return category is "Community" or "Race" or "Superfamily";
		}

		// Links captured NpcSpawns to the resolved area whose creature + bounds they match. Resets links on the touched
		// maps first (idempotent), so a re-resolve reflects the current creature assignments.
		private static async Task<int> LinkCapturedSpawns(DatabaseContext db, List<NpcSpawnArea> areas)
		{
			const float pad = 5f;
			var resolved = areas.Where(a => a.NpcTemplateId != null).ToList();
			var maps = resolved.Select(a => a.MapId).ToHashSet();
			var linked = 0;
			foreach (var mapId in maps)
			{
				var mapAreas = resolved.Where(a => a.MapId == mapId).ToList();
				foreach (var s in await db.NpcSpawns.Where(s => s.MapId == mapId).ToListAsync())
				{
					var area = mapAreas.FirstOrDefault(a =>
						a.NpcTemplateId == s.NpcTemplateId && s.PositionX >= a.BoundsMinX - pad && s.PositionX <= a.BoundsMaxX + pad && s.PositionZ >= a.BoundsMinZ - pad &&
						s.PositionZ <= a.BoundsMaxZ + pad);
					var newLink = area?.Id;
					if (s.NpcSpawnAreaId != newLink)
						s.NpcSpawnAreaId = newLink;
					if (newLink != null)
						linked++;
				}
			}

			await db.SaveChangesAsync();
			return linked;
		}

		// The name AGREES with the id's creature if any creature-word (a token of the creature name, or a defining
		// family, singular forms included) is a substring of the group's name family or vice-versa. So "wildwolf"
		// agrees with "Wolf", "chickenroam" with "Chicken", but "slaver" does NOT agree with "Poacher".
		private static bool NameAgrees(string fam, string creatureName, HashSet<string> definingFamilies)
		{
			if (fam.Length == 0)
				return true;

			var words = new HashSet<string>(definingFamilies);
			foreach (Match m in Word.Matches(creatureName.ToLowerInvariant()))
				words.Add(m.Value);
			foreach (var w in words.ToList())
				words.Add(Singularize(w));

			return words.Any(w => w.Length >= 3 && (fam.Contains(w, StringComparison.Ordinal) || w.Contains(fam, StringComparison.Ordinal)));
		}

		// Tier 1: the group name (or Name-category id, 4-char tag stripped) -> a spawnable template. Tries the name as
		// given, then singularized (Rabbits -> Rabbit).
		private static uint? NameResolve(NpcSpawnArea a, Dictionary<string, uint> templateByName)
		{
			if (string.Equals(a.Category, "Name", StringComparison.OrdinalIgnoreCase))
			{
				var tagStripped = FourCharTag.Replace(a.GroupKey ?? "", "").Trim();
				if (templateByName.TryGetValue(tagStripped, out var byId))
					return byId;
			}

			var nm = Family(a.GroupName);
			if (nm.Length > 0 && templateByName.TryGetValue(nm, out var t))
				return t;

			var sing = Singularize(nm);
			if (sing.Length > 2 && templateByName.TryGetValue(sing, out var ts))
				return ts;

			return null;
		}

		private static string Singularize(string s)
		{
			return s.EndsWith('s')
				? s[..^1]
				: s;
		}
	}

	public readonly record struct ResolveStats(int Name, int Containment, int IdProp, int RaceUnique, int Total, int Areas, int Linked);
}

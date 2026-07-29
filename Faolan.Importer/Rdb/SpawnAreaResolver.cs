using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Rdb
{
	// Pre-fills each NpcSpawnArea's creature (NpcTemplateId) from what we CAN infer, since the RDB carries WHERE + how
	// many but never WHICH (#363). Three confidence tiers, applied in order, never overwriting a Manual assignment:
	//   1. Name         - the group name / Name-id (4-char tag stripped) matches a spawnable NpcTemplate by name.
	//   2. Containment  - the most-common captured NpcSpawn inside the roam bounds (unanimous, >= 2). Captured maps only.
	//   3. IdPropagation- an id->creature learned from tiers 1-2, propagated to a same-id group, doubly GUARDED:
	//                     the group's own name must not contradict the id's creature (Slaver1 under Poacher's
	//                     Community 60 is left alone), AND the id must have been resolved by tier 1-2 on THAT MAP
	//                     (#489) - a numeric id names a family, not a species, so an observation on one playfield
	//                     says nothing about the same family on another.
	// Machine assignments are RESET at the start, so this is idempotent and a rules change reaches existing rows;
	// a Manual assignment is never touched. Measured coverage: 186 of 2,664 areas. The rest await captures / a
	// manual `.setnpcgroup` pass - honestly unknown rather than wrongly guessed.
	public static class SpawnAreaResolver
	{
		private const float ContainPad = 10f; // captured-spawn search: pad the roam AABB by this (world units), X/Z
		private static readonly Regex FourCharTag = new(@"\s*\([0-9A-Za-z]{4}\)\s*$", RegexOptions.Compiled);

		// Name families that carry no creature claim - a group so named may take a propagated creature even if its name
		// doesn't match (it's a location/role label, not a species). Keeps the guard from blocking legitimate propagation.
		private static readonly Regex TerritoryLabel = new(
			@"territory|area|mound|burial|captives|boss|library|treasury|infirmary|scout|party|trash|point|patrol|roaming|citizen|spawngroup|zone|region|wave|node|camp", RegexOptions.Compiled);

		private static readonly Regex TrailingIndex = new(@"[\s_\-]*\d+\s*$", RegexOptions.Compiled);
		private static readonly Regex Word = new(@"[a-z]+", RegexOptions.Compiled);

		public static async Task<ResolveStats> ResolveAsync(DatabaseContext db)
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

				var byTemplate = inside.GroupBy(c => c.TemplateId).ToList();
				if (byTemplate.Count != 1)
					continue; // not unanimous

				a.NpcTemplateId = byTemplate[0].Key;
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

			await db.SaveChangesAsync();

			// #363 auto-link: materialize which captured NpcSpawns each resolved area OWNS (same creature, inside the
			// bounds), so graduation is a clean FK delete instead of a re-derived bounds match. Runs on the maps that
			// have resolved areas (the captured ones); uncaptured maps have no spawns to link.
			var linked = await LinkCapturedSpawns(db, areas);

			return new ResolveStats(name, contain, idprop, name + contain + idprop, areas.Count, linked);
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

	public readonly record struct ResolveStats(int Name, int Containment, int IdProp, int Total, int Areas, int Linked);
}

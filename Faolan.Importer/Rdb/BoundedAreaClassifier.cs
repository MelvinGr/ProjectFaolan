using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Faolan.Importer.Rdb
{
	// Sorts the client's bounded-area regions (RDB 1000053, see BoundedAreaCollection) into the families the survey in
	// docs/rdb/bounded-areas-1000053.md is organised by. 8,300-odd regions over 478 playfields, and the ONLY thing a
	// region carries about its purpose is its NAME - so this is a naming heuristic and nothing more.
	//
	// ⚠️ It is a SURVEY AID, NOT AN IMPORT GATE (#513). Landing a bucket in the DB needs that family's own evidence -
	// a capture, a client script, a cross-check against data we already hold. RdbHazardImporter imports lava and only
	// lava for exactly this reason: the other hazard-shaped names are hazard-shaped BY NAME, and a mis-guessed kill
	// volume silently kills players on a map nobody is testing. Do not wire a bucket straight into an importer because
	// the classifier is confident - it has no way to be.
	//
	// Rules are ordered and FIRST MATCH WINS, so the buckets are mutually exclusive and the census sums to the total.
	// Order is load-bearing and encodes which signal in a name is the stronger claim:
	//   - the structural families first (collision, audio) - they are the client's own generated names and never a
	//     gameplay region, so `Vanir_camp_music` is audio, not a camp;
	//   - then the families whose word is unambiguous (resurrection, teleport, dismount);
	//   - then the hazards, lava first, because that is the one family the wire pins;
	//   - then the gameplay regions whose names overlap heavily (quest > encounter > camp): `BA_QuestSpawn_TheDevilsLight`
	//     is a quest region that happens to say "spawn", and `BossSpawnTrigger` is a trigger that happens to say "spawn".
	// A name that reaches the end is Uncategorised, which is 4,000-odd of them - honestly unknown, not forced into a
	// bucket.
	public static class BoundedAreaClassifier
	{
		// One rule per bucket, applied in this order. The regexes are deliberately readable rather than clever: this
		// table is the classification, so it has to be reviewable as prose.
		private static readonly (BoundedAreaBucket Bucket, Regex Match)[] Rules =
		[
			// The client's own generated collision volumes (`BoundedAreaCollision7`, `BoundedAreaCollision_-3`) plus the
			// hand-named blockers. Checked first: these are camera/pathing geometry, never a gameplay trigger.
			(BoundedAreaBucket.Collision, Rx(@"collision|blocker|\bblock(ing)?\b|nopathing|walkblock")),

			// Audio: the generated SFX layers (`BoundedAreaSFX_Fixed3D0`, `BoundedAreaSFX_EAX_1`, `BoundedAreaSFX_Music0`)
			// and the hand-named music/ambience regions. Before every gameplay bucket, so an audio TWIN of a real region
			// (`Vanir_camp_music`, `BA_Lava_music`) lands here rather than shadowing the region it is named after - the
			// same distinction RdbHazardImporter's NotAVolume guard draws for lava.
			(BoundedAreaBucket.Audio, Rx(@"sfx|_music|music_|\bmusic\b|reverb|reveb|\beax\b|ambien(ce|t)|soundscape")),

			// Resurrection pads. The corpus spells them several ways and only two of them contain the word:
			// `BA_Resurrection_*`, `RezBA`/`RezzBaishan`, `Pin Pin Respad`, `BA_MountainGlade_RezDiscovery`,
			// `BA_BottomFloor_RezPointDiscover`, and a bare `-res` SUFFIX (`graveyard_res`, `LoggingCampRes`,
			// `Throneroomres`). The suffix is anchored at the end precisely because a loose `res` is a substring of half
			// the place names in the game.
			//
			// ⚠️ `_` is a WORD character, so `\brez` does NOT match `zang_xin_camp_rezpad` OR
			// `BA_MountainGlade_RezDiscovery`. It used to be written `\brez` plus an explicit `re[zs]pad` to paper over
			// the first case, which left the second: every `*_RezDiscovery` fell through to the QuestGoal rule's
			// `discovery` and the whole discovery family - the one this bucket is most about - was filed as quest
			// regions. Anchor on `(?<![a-z])` and the underscore stops mattering.
			(BoundedAreaBucket.Resurrection, Rx(@"resurrect|rezz|(?<![a-z])rez|re[zs]pad|res$|_res(?![a-z])|(?<![a-z])res(pawn|point|prison|spot|zone)")),

			(BoundedAreaBucket.Teleport, Rx(@"teleport|\bport(al)?\b|zoneline|zone_line|\bwarp\b|traveltrigger")),

			(BoundedAreaBucket.Dismount, Rx(@"dismount|nomount|no_mount")),

			// Hazards. Lava first - it is the only family the corpus pins (see MapHazard) and the only one imported.
			(BoundedAreaBucket.HazardLava, Rx(@"lava")),
			(BoundedAreaBucket.HazardTrap, Rx(@"\btrap\b|trapba|spiketrap|\bspikes?\b|snakepit")),
			(BoundedAreaBucket.HazardDrown, Rx(@"drown|waterdamage|water_damage|underwater_dmg")),
			(BoundedAreaBucket.HazardFireDeath, Rx(@"\bfire\b|burn|incinerat|poisonfog|\bdeath\b|_dies|killarea|\bdamage\b|\bdmg\b|hazard")),

			// Quest regions before encounters: a quest name that also says "spawn"/"trigger" is still a quest region.
			(BoundedAreaBucket.QuestGoal, Rx(@"quest|\bgoal\b|objective|\bq_\b|_q\d|reacharea|discovery")),

			// Encounter + event START triggers: the ones retail fires an encounter from. Deliberately BEFORE camps,
			// because an ambush/boss trigger sitting inside a camp is a trigger first.
			(BoundedAreaBucket.Encounter, Rx(@"trigger|ambush|encounter|\bevent\b|bossfight|miniboss|\bboss\b|\bfight\b|_start|start_|startba|\bsensor\b|ringspawn|despawn|\bwave\d|cutscene|scene\b")),

			// What is left that says "camp" is a mob camp - a named settlement of creatures. `spawn` on its own is NOT
			// enough (it is in despawn triggers, collision names and quest hooks alike, all of which are claimed above);
			// the surviving spawn-* names here are the mob-placement ones (`Mob spawn center 1`, `StaticSpawns`).
			(BoundedAreaBucket.SpawnCamp, Rx(@"\bcamps?\b|camp\d|\d camp|_camp|camp_|[a-z]camp|spawn"))

			// There is NO rule for the survey's "zone / area names" family, deliberately. Those regions are named after
			// the PLACE (`Acheronian Ruins`, `The Iron Tower`, `Onyx Chambers`), so the only test for one is whether the
			// name matches the place-name catalog - a data lookup, not a naming pattern, and the banner ids in MapArea
			// are LDB string ids rather than text. Identifying them is #348's job; a regex here would have matched
			// nothing and printed a zero row that reads as "the client ships none", which is the opposite of the truth.
		];

		// The client-side scry script a region runs to tell the SERVER the player crossed its boundary. 479 regions
		// over 60 playfields share it, and what the server does on that notification is the region's own business:
		// it is the mechanism behind the area-name banners (#348) and it also appears on dismount volumes, a couple
		// of fire hazards and a respawn-discovery area.
		//
		// ⚠️ So it is NOT "the banner script" - it is "this volume talks to the server", which is a strictly weaker
		// and more useful claim. It is a FILTER, not an identification: a region without it cannot be what fires a
		// server-sent banner, so it excludes the audio regions, building interiors and playfield borders that a
		// containment test alone happily returns. Pairing it with a captured banner is what identifies one.
		//
		// Found by grouping every 1000053 region by ScryId: on Old Tarantia it selects exactly the `BA_*` sub-areas
		// (BA_Bridge, BA_MarketWard, BA_EastGate, BA_WestGate, BA_Docks, BA_GreatLibrary, ...), on Tarantia Noble
		// District the named quarters (Noble Quarters, The Market, Castle Approach, The Southern Bridge) and on Field
		// of the Dead the Clan burial mounds - three playfields whose sub-area names are the banner texts.
		public const uint CrossingNotifierScryId = 220656;

		private static Regex Rx(string pattern)
		{
			return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
		}

		public static BoundedAreaBucket Classify(string name)
		{
			foreach (var (bucket, match) in Rules)
				if (match.IsMatch(name))
					return bucket;

			return BoundedAreaBucket.Uncategorised;
		}

		// The buckets in classification order, so a census prints in the order the rules are applied (which is the order
		// that explains any surprising assignment).
		public static IReadOnlyList<BoundedAreaBucket> Order { get; } =
		[
			.. Array.ConvertAll(Rules, r => r.Bucket),
			BoundedAreaBucket.Uncategorised
		];

		// The `--report-arg` slug for a bucket: the enum name lowercased with '-' before each inner capital
		// (HazardLava -> hazard-lava). Kept derived rather than a second table that can drift out of step.
		public static string Slug(BoundedAreaBucket bucket)
		{
			return Regex.Replace(bucket.ToString(), "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
		}

		public static BoundedAreaBucket? FromSlug(string slug)
		{
			foreach (var b in Order)
				if (string.Equals(Slug(b), slug, StringComparison.OrdinalIgnoreCase))
					return b;

			return null;
		}
	}

	public enum BoundedAreaBucket
	{
		Collision,
		Audio,
		Resurrection,
		Teleport,
		Dismount,
		HazardLava,
		HazardTrap,
		HazardDrown,
		HazardFireDeath,
		QuestGoal,
		Encounter,
		SpawnCamp,
		Uncategorised
	}
}

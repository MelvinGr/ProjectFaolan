using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;

namespace Faolan.Importer.Modules
{
	// RDB-sourced modules. Opt-in (Source == Rdb keeps them out of the default set); all require --rdb. The two
	// with out-of-DB write side effects (rdb-respawns -> mirror-store blobs, rdb-items -> --dump-templates) suppress
	// that write under --dry-run.

	// Read-only inventory of the RDB resource types (writes nothing). The friendly names cover the resource
	// types we care about (item-template / NPC-stat / dialogue / playfield).
	public sealed class RdbInventoryModule : IImportModule
	{
		private static readonly Dictionary<uint, string> TypeNames = new Dictionary<uint, string>
		{
			[0] = "ScriptGroup",
			[1000010] = "Filenames",
			[1000077] = "ContentSpawnDataExport",
			[1000078] = "AcgSpawnDataExport",
			[1000079] = "NpcSpawnDataExport",
			[1000083] = "TreasureDataExport",
			[1000090] = "ResurrectionPointsExport",
			[1010001] = "3DModel",
			[1010004] = "Texture/FCTX",
			[1010035] = "TextScript",
			[1010201] = "MonsterData",
			[1010223] = "LuaArchive",
			[1010700] = "CharacterCreator",
			[1020003] = "Lip",
			[1030002] = "TextStrings",
			[1070003] = "PlayfieldDescription"
		};

		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Read-only RDB resource-type inventory.";
		public string Name => "rdb-inventory";

		public Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!, ctx.RdbProxyBase);
			ctx.Out.WriteLine($"RDB: {rdb.Count} index entries in {ctx.RdbDir}");
			ctx.Out.WriteLine("  type        count   name");
			foreach (var (type, count) in rdb.TypeCounts())
				ctx.Out.WriteLine($"  {type,-10}  {count,6}  {(TypeNames.TryGetValue(type, out var n) ? n : "")}");

			// #834: a (type, id) the index states twice means an override layer is present, and TryGet's last-wins
			// rule is deciding which copy every consumer gets. Say so where a human is looking, rather than leaving
			// it to be discovered as wrong content. Zero on this client - printed anyway, because "0 duplicate keys"
			// is the answer to the question and a silent absence is not.
			ctx.Out.WriteLine($"  duplicate (type,id) keys: {rdb.DuplicateKeys.Count}"
				+ (rdb.DuplicateKeys.Count == 0
					? ""
					: " - TryGet serves the LAST entry of each"));
			foreach (var (type, id, count) in rdb.DuplicateKeys.Take(20))
				ctx.Out.WriteLine($"    {type}:{id} x{count}");

			return Task.FromResult(new ImportResult($"{rdb.Count} index entries inventoried, {rdb.DuplicateKeys.Count} duplicate keys (read-only)."));
		}
	}

	public sealed class RdbMorphsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Morph-target registry from the RDB.";
		public string Name => "rdb-morphs";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbMorphTargetImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{n} morph targets imported.");
		}
	}

	public sealed class RdbAppearanceModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Character-creator appearance choices (per sex/race hair/colours/tattoos/heads + names) from the RDB.";
		public string Name => "rdb-appearance";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var n = await RdbAppearanceImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{n} appearance choices imported.");
		}
	}

	public sealed class RdbSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		// It writes `Spells.DurationMs` from property field 8 unconditionally, and a family of records leaves that
		// field empty - so an rdb-spells run on its own reinstates the 0/1 ms rows `buff-durations` fixed (#659).
		public IReadOnlyList<string> RequiredAfter => [nameof(BuffDurationsModule)];

		public string Description => "Named spell records (cast time/name) + the mount catalog from the RDB.";
		public string Name => "rdb-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbSpellImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Seen} named records, {r.Inserted} new, {r.NamesSet} names, {r.CastTimesSet} cast times, {r.LevelsSet} levels, {r.Mounts} mounts, {r.EffectDurations} effect-catalog durations, {r.EffectLinks} effect links, {r.UnparsedElements} UNPARSED element heads; {r.UnparsedSamples}.");
		}
	}

	// #291/#300: backfill EMPTY spell names from the localized-string DB (LDB, RDB 1030002), English page picked by
	// cribbing against the catalog's existing template names. Runs after rdb-spells so those names exist to crib on.
	// #303: the abilities the client knows but no capture ever granted. Identity only - see RdbLdbAbilityImporter for
	// why a row from here cannot be granted (the 0x2052 grant carries the TEA key, and the keys are random).
	public sealed class RdbLdbAbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(RdbLdbNamesModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Catalog every ability the client knows, named from the English LDB page (identity only - not grantable).";
		public string Name => "rdb-ldb-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbLdbAbilityImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Total} abilities in the client; {r.AlreadyKnown} already catalogued, {r.Added} added (identity only).");
		}
	}

	public sealed class RdbLdbNamesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(RdbSpellsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Backfill empty spell names from the English LDB page (RDB 1030002).";
		public string Name => "rdb-ldb-names";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbLdbNameImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"spells: {r.SpellEnglish} English names, filled {r.SpellsFilled}; items: {r.ItemEnglish} English names, filled {r.ItemsFilled}.");
		}
	}

	// The ACHIEVEMENT catalog (#748) - LDB 55701 names + 55700 descriptions, keyed by the id the wire calls a TAG.
	// Without it a granted achievement is a bare number: `DispatchAddTag` sends one u32 and nothing else.
	// #310: the Khitai faction catalog + its kill-payout matrix. Opt-in like every RDB module, and dependency-free -
	// it is keyed by the client's own faction ids and joins to nothing we import.
	public sealed class RdbFactionsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Khitai faction catalog + kill-payout matrix (FactionRelations.xml 1010701 + LDB 254).";
		public string Name => "rdb-factions";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbFactionImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Factions} faction(s) ({r.Named} named from the LDB, {r.Unjoined} unjoined), {r.Awards} kill-award row(s).");
		}
	}

	public sealed class RdbAchievementsModule : IImportModule
	{
		// No dependency: the catalog is keyed by the client's own tag ids and joins to nothing.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "The achievement catalog (LDB 55701 names / 55700 descriptions), keyed by tag id.";
		public string Name => "rdb-achievements";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbAchievementImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Names} achievement name(s), {r.Descriptions} description(s), {r.Written} row(s) written.");
		}
	}

	// Ability names from the RDB 1000020 templates, decrypted with each ability's own harvested Data1..4 TEA key.
	public sealed class RdbAbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(AbilitiesModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Ability names from the RDB 1000020 templates (TEA-decrypted with the 0x2052 key).";
		public string Name => "rdb-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (decrypted, namesSet) = await RdbAbilityImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{decrypted} templates decrypted, {namesSet} names set.");
		}
	}

	// Feat catalog (id / name / tree / group / rank) from the RDB 1000020 PLAINTEXT templates - no TEA key needed.
	public sealed class RdbFeatsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Feat catalog (name/tree/group/rank) from the RDB 1000020 plaintext templates.";
		public string Name => "rdb-feats";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbFeatImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Found} feat templates ({r.Unnamed} unnamed, skipped); {r.Added} added, {r.Updated} updated.");
		}
	}

	// Combo catalog (name / unlock level / featid / actions / ...) from the RDB 1010206 plaintext-XML combo defs.
	public sealed class RdbCombosModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Combo catalog (name/level/featid/actions) from the RDB 1010206 plaintext XML.";
		public string Name => "rdb-combos";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, imported) = await RdbComboImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{seen} combo XML entries, {imported} combos imported.");
		}
	}

	public sealed class RdbMapsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Map names + ids from the RDB playfield descriptions.";
		public string Name => "rdb-maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbMapImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Found} playfields ({r.Gated} unreachable); {r.Created} created, {r.Renamed} renamed.");
		}
	}

	// The client's OWN quest catalog (RDB 1000104, #569) - 2181 quest lines / 5042 quests / 7301 goals with the
	// designers' names in plaintext. Everything else in the quest tables came from captures, so it covered only what a
	// captured character held; this covers the game. It ADDS and NAMES and never overwrites captured wire data.
	public sealed class RdbNameFilterModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert,
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "The client's forbidden-word list for character names (RDB 1065100), stored HASHED.";
		public string Name => "rdb-name-filter";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbNameFilterImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase, ctx.ModeFor(Name) == WriteMode.Overwrite);
			return new ImportResult($"{r.Shipped} terms shipped, {r.Usable} usable after dedupe; {r.Added} added.");
		}
	}

	public sealed class RdbQuestCatalogModule : IImportModule
	{
		// Lines must exist before the quests that FK them, and quests before their goals - the importer orders those
		// three passes itself. It depends on no other module: the catalog is self-contained client data.
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Quest lines/quests/goals + their names from the client's quest catalog (RDB 1000104).";
		public string Name => "rdb-quest-catalog";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbQuestCatalogImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult(
				$"lines +{r.LinesAdded} ({r.LinesNamed} named, {r.PlaceholderLines} placeholder); quests +{r.QuestsAdded} ({r.QuestsNamed} named); " +
				$"goals +{r.GoalsAdded} ({r.GoalsNamed} named); {r.Rewards} item rewards; {r.GoalsOrphaned} goals with no quest.");
		}
	}

	// Resurrection points; on-demand records fetched from the CDN. Under --dry-run the CDN blob archive
	// (--store) is suppressed (an out-of-DB write the transaction rollback can't undo).
	public sealed class RdbRespawnsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Resurrection points (CDN-backed).";
		public string Name => "rdb-respawns";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbRespawnImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			return new ImportResult($"{r.Points} points across {r.Maps} maps ({r.Empty} empty, {r.FetchFailed} fetch failures).");
		}
	}

	// Quest wiring read from the goals' own DECRYPTED objective text (#520/#521). RDB-backed, so it is opt-in and
	// separate from the capture-driven `quest-wiring`.
	public sealed class RdbQuestTextModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(QuestsModule), nameof(NpcsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;
		public bool InDefaultSet => false;

		public string Description => "Quest wiring from the goals' decrypted objective text + typed target reference (#520/#521, opt-in).";
		public string Name => "rdb-quest-text";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbQuestTextImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			// The target fill runs SECOND on purpose. It claims every remaining bare Kill goal whose trailing reference
			// is an npc_template, which makes the goal "wired" - so running it first would hide a talk/deliver shape
			// from the objective-text pass above, which is the only thing that can retype one.
			var t = await RdbQuestGoalTargetImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);

			// Every goal it did NOT wire is accounted for by reason - a silent skip here is indistinguishable from a
			// decode regression.
			return new ImportResult(
				$"{r.Wired} goal(s) typed from their objective text ({r.WiredWithoutNpc} of them naming an NPC we do not hold yet, " +
				$"so inert until its map is imported, {r.WiredWithoutDialogue} held but with no dialogue captured so the talk " +
				$"cannot fire yet); {r.AlreadyWired} already wired, {r.PreconditionGated} carrying a recorded rank/level precondition, " +
				$"{r.NotOurShape} not a deliver/talk shape, {r.NoNameInText} no name in the text, {r.Unreadable} unreadable, of {r.Goals} goals; " +
				$"{r.ReconciledToTheCatalogName} short name(s) reconciled to the NpcTemplate's full name (a match key must EQUAL the live name). " +
				$"Targets: {t.FromCatalog} named from the NpcTemplate the reference points at ({t.PlaceholderKept} of those a shared " +
				$"placeholder name the LDB disagrees with), {t.FromLdb} from LDB[51000] for a template we do not hold, " +
				$"{t.AlreadyWired} already wired, {t.Unresolved} with no English LDB entry, of {t.Goals} npc-referencing goals.");
		}
	}

	// #635: the `{teleportplayer}` travellers, whose destination is inline in their dialogue TEXT rather than in a paid
	// destination list. RDB-backed for the reason every module in this file is - the text is in the client's install
	// and the capture modules run without an RdbDir - and, more sharply, because the directive never reaches the
	// server at runtime: a conversation screen carries only an LDB reference, and the server ships no LDB.
	public sealed class RdbDialogueTeleportsModule : IImportModule
	{
		// The screens to read and the edges to key the rows by both come from the conversation graph; the maps have to
		// exist for a destination to be resolvable. Both come in through `travel-wiring`, which is named directly
		// rather than reached transitively: it OWNS this table's captured rows, and the conversation rebuild it sits
		// behind cascades them away (TravelDestination FKs ConversationNode). Depending on `conversations` alone left
		// a `--only rdb-dialogue-teleports` run with the four captured wagoner trips deleted and only these three
		// directive trips put back.
		public IReadOnlyList<string> DependsOn => [nameof(TravelWiringModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;
		public bool InDefaultSet => false;

		public string Description => "Local travel from inline {teleportplayer} dialogue directives (#635, opt-in).";
		public string Name => "rdb-dialogue-teleports";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbDialogueTeleportImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);

			// Every directive it did NOT turn into a trip is accounted for by reason - a silent skip is
			// indistinguishable from a decode regression.
			return new ImportResult(
				$"{r.Directives} {{teleportplayer}} directive(s) across {r.Screens} screen(s): {r.Added} trip(s) added, {r.Updated} updated, " +
				$"{r.CapturedWins} left to an existing CAPTURED trip on the same branch, {r.NoInboundEdge} on a screen no branch reaches, " +
				$"{r.UnknownMap} naming a playfield we have not imported; {r.Unreadable} screen(s) failed to decrypt.");
		}
	}

	// The bounded-area trigger volumes a subsystem actually consumes (RDB 1000053, #519/#521/#513/#504), into
	// MapRegion. Clean rebuild - the set is derived from the fixed client RDB - so Overwrite-only.
	//
	// Depends on rdb-respawns and the quest import: it links regions to the RespawnPoints and QuestGoalDefinitions
	// that are already in the DB, so running it against a database without them imports nothing and reports so.
	//
	// The LAVA volumes come through here too (#679). They used to have a module of their own, `rdb-hazards`, over a
	// MapHazard table that carried its own copy of the geometry; a hazard is now a BEHAVIOUR side table on the region,
	// so one importer owns the 1000053 geometry and there is nothing left for a second module to rebuild.
	public sealed class RdbRegionsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => ["rdb-respawns"];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Consumed bounded-area trigger volumes -> MapRegion (1000053, #519/#521/#513).";
		public string Name => "rdb-regions";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			// The captured banner sightings are harvested here and handed IN - they are an input to the match, not
			// state (#680): a banner ends up as a column on the region that fires it, so nothing about one is read
			// back out of the database.
			var captured = AreaNameExtractor.Merge(ctx.Captures.SelectMany(c => AreaNameExtractor.Extract(c.Packets)));
			var r = await RdbRegionImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase, captured);
			// Anything a consumer asked for and did not get is named, not swallowed.
			foreach (var note in r.Notes)
				ctx.Out.WriteLine($"  rdb-regions unresolved: {note}");

			return new ImportResult($"{r.Regions} regions ({r.Vertices} vertices); linked {r.DiscoveryLinks} respawn discovery volume(s), " +
			                        $"{r.GoalLinks} quest goal region(s), {r.Hazards} lava hazard(s) and {r.NoMount} no-mount volume(s); " +
			                        $"{r.Banners} area banner(s) ({r.GeometricBanners} matched geometrically, {r.DroppedBanners} captured banner(s) dropped).");
		}
	}

	// Authoritative open-world NPC spawn points from the RDB "NPC roam/movement" layout (type 1010506, #332). Clean
	// rebuild - the set is fully derived from the fixed client RDB, so Overwrite-only.
	public sealed class RdbNpcSpawnsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Authoritative open-world ambient spawn areas (RDB 1010506 layout, #332/#363).";
		public string Name => "rdb-npc-spawns";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var r = await RdbNpcSpawnImporter.ImportAsync(ctx.Db, ctx.RdbDir!, ctx.RdbProxyBase);
			var res = r.Resolved;
			return new ImportResult($"{r.Areas} spawn areas ({r.Points} points) across {r.Maps} playfields ({r.EmptyMaps} empty, {r.Unreadable} unreadable); " +
			                        $"resolved {res.Total}/{res.Areas} areas (name {res.Name}, containment {res.Containment}, id-prop {res.IdProp}, race-unique {res.RaceUnique}); linked {res.Linked} captured spawns.");
		}
	}

	// Decrypts each keyed item's RDB template. Under --dry-run the --dump-templates disk write is suppressed.
	public sealed class RdbItemsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Decrypt keyed item RDB templates (names/categories/values).";
		public string Name => "rdb-items";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var dump = ctx.DryRun
				? null
				: ctx.DumpTemplatesDir;
			var r = await RdbItemImporter.ImportAsync(ctx.Db, ctx.RdbDir!, dump, ctx.RdbProxyBase);
			return new ImportResult(
				$"{r.Decrypted}/{r.Keyed} decrypted; {r.NamesAdded} names, {r.CategoriesAdded} categories, {r.ValuesAdded} values, {r.QualitiesAdded} qualities, {r.StatsAdded} stat mods.");
		}
	}
}

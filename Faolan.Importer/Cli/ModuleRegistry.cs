using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Modules;

namespace Faolan.Importer.Cli
{
	// The catalog of import modules + selection/dependency resolution. Each module wraps a CaptureX/Rdb
	// importer or extractor (see docs/tooling/importer.md). Registration order is the stable tie-break for the
	// topo sort.
	public static class ModuleRegistry
	{
		// Registered in default-run order so a no-flag run sequences the same every time (the topo sort is
		// stable by this order). RDB + mounts are opt-in (InDefaultSet == false).
		public static IReadOnlyList<IImportModule> All { get; } =
		[
			// --- capture modules ---
			new ListingsModule(), new StockPhrasesModule(), new ItemsModule(), new MapsModule(), new SpellsModule(), new StatDamageModule(), new SpellEffectsModule(), new AbilitiesModule(),
			new StartingSpellsModule(),
			new CharactersModule(), new ConversationsModule(), new CharacterStateModule(), new LearnedSpellsModule(), new CharacterAbilitiesModule(), new CharacterQuestsModule(),
			new CharacterBankModule(), new ShopsModule(), new NpcsModule(), new NpcDispositionModule(), new NpcDeathBehaviorsModule(), new NpcChatterModule(), new WorldObjectsModule(),
			new WorldTeleportsModule(), new AreaNamesModule(), new MapLootModule(), new QuestsModule(), new QuestWiringModule(), new TravelWiringModule(), new MountsModule(), // opt-in
			new PetsModule(), // opt-in (#307)
			new ConsumableUsesModule(), // consumable on-use spells -> Items.OnUseSpell
			new ClaimsModule(), // opt-in (account claims -> AccountClaim)
			new RecipesModule(), // opt-in (crafting recipe book)
			// --- tsv modules ---
			// The ellatha + AoC-fandom-wiki scrapes are GONE: the RDB leads, and it turned out to carry both facts
			// they were standing in for - a spell's class membership (the class-encoded GuiStackingLine, RdbSpellImporter)
			// and a combo's reuse time (the combo XML's own `cooldown`, RdbComboImporter). See Gitea #296.
			new StarterSpellLevelsModule(), // capture-sourced, and the create kit is a LIST, not a level query
			// --- rdb modules (opt-in, require --rdb) ---
			new RdbInventoryModule(), new RdbMorphsModule(), new RdbAppearanceModule(), new RdbSpellsModule(), new RdbLdbNamesModule(), new RdbLdbAbilitiesModule(), new RdbAbilitiesModule(), new RdbCombosModule(), new RdbFeatsModule(),
			new RdbMapsModule(), new RdbRespawnsModule(), new RdbQuestCatalogModule(), new RdbItemsModule(), new RdbNpcSpawnsModule(), new RdbHazardsModule(), new RdbQuestTextModule(), new RdbRegionsModule(), new ClassAttributeBaseModule(), new ClassComboScalingModule(),
			// --- corrections that must outrank an rdb module ---
			// LAST on purpose: rdb-spells writes Spells.DurationMs unconditionally, so a capture-sourced duration for
			// the rows its property run leaves empty has to be applied after it (#659). Registration order is the
			// topo sort's tie-break, so this placement IS the ordering.
			new BuffDurationsModule()
		];

		// The default set when no --only is given: every module that opts in (RDB + mounts are opt-in).
		public static IReadOnlyList<IImportModule> DefaultSet => All.Where(m => m.InDefaultSet).ToList();

		// Resolves a module by its CLI Name ("items", from --only/--skip) OR by its class name (nameof(ItemsModule),
		// the compile-checked key DependsOn uses so a renamed/removed module breaks the build, not a run).
		public static IImportModule? Find(string name)
		{
			return All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.GetType().Name, name, StringComparison.OrdinalIgnoreCase));
		}

		// Expands a requested set with its transitive dependencies, then returns it in dependency order
		// (deps before dependents). Registration order in All breaks ties so runs are deterministic.
		public static IReadOnlyList<IImportModule> Plan(IEnumerable<IImportModule> requested)
		{
			// 1. Transitive closure over DependsOn (what a module needs) AND RequiredAfter (what must happen because
			//    it ran - a module that leaves the database in a state nobody should stop at names its completer).
			var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var pending = new Stack<IImportModule>(requested);
			while (pending.Count > 0)
			{
				var m = pending.Pop();
				if (!wanted.Add(m.Name))
					continue;

				foreach (var dep in m.DependsOn)
					pending.Push(Find(dep) ?? throw new InvalidOperationException($"Module '{m.Name}' depends on unknown module '{dep}'."));
				foreach (var after in m.RequiredAfter)
					pending.Push(Find(after) ?? throw new InvalidOperationException($"Module '{m.Name}' requires unknown module '{after}' to run after it."));
			}

			// 2. Topological sort (DFS), iterating All so the output order is stable.
			var ordered = new List<IImportModule>();
			var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var m in All.Where(m => wanted.Contains(m.Name)))
				Visit(m);

			return ordered;

			void Visit(IImportModule m)
			{
				if (done.Contains(m.Name))
					return;

				if (!onStack.Add(m.Name))
					throw new InvalidOperationException($"Dependency cycle through module '{m.Name}'.");

				foreach (var dep in m.DependsOn)
					Visit(Find(dep)!);
				onStack.Remove(m.Name);
				done.Add(m.Name);
				ordered.Add(m);
			}
		}
	}
}

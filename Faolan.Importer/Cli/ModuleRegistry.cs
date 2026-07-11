using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Modules;

namespace Faolan.Importer.Cli
{
	// The catalog of import modules + selection/dependency resolution. Each module wraps a CaptureX/Rdb
	// importer or extractor (see docs/tooling/importer-plan.md). Registration order is the stable tie-break for the
	// topo sort.
	public static class ModuleRegistry
	{
		// Registered in default-run order so a no-flag run sequences the same every time (the topo sort is
		// stable by this order). RDB + mounts are opt-in (InDefaultSet == false).
		public static IReadOnlyList<IImportModule> All { get; } =
		[
			// --- capture modules ---
			new ListingsModule(), new StockPhrasesModule(), new ItemsModule(), new MapsModule(), new SpellsModule(), new StatDamageModule(), new AbilitiesModule(), new StartingSpellsModule(),
			new CharactersModule(), new ConversationsModule(), new CharacterStateModule(), new LearnedSpellsModule(), new CharacterAbilitiesModule(), new CharacterQuestsModule(),
			new CharacterBankModule(), new ShopsModule(), new NpcsModule(), new NpcDispositionModule(), new NpcChatterModule(), new WorldObjectsModule(), new MapLootModule(), new QuestsModule(), new MountsModule(), // opt-in
			new PetsModule(), // opt-in (#307)
			new RecipesModule(), // opt-in (crafting recipe book)
			// --- tsv modules ---
			new EllathaLevelsModule(), new EllathaCombosModule(), new WikiClassSpellsModule(), new WikiSpellLevelsModule(),
			new StarterSpellLevelsModule(), // capture-sourced but runs late: overrides the ellatha/wiki levels
			// --- rdb modules (opt-in, require --rdb) ---
			new RdbInventoryModule(), new RdbMorphsModule(), new RdbAppearanceModule(), new RdbSpellsModule(), new RdbAbilitiesModule(), new RdbCombosModule(), new RdbMapsModule(), new RdbLuaModule(),
			new RdbRespawnsModule(), new RdbItemsModule()
		];

		// The default set when no --only is given: every module that opts in (RDB + mounts are opt-in).
		public static IReadOnlyList<IImportModule> DefaultSet => All.Where(m => m.InDefaultSet).ToList();

		public static IImportModule? Find(string name)
		{
			return All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		// Expands a requested set with its transitive dependencies, then returns it in dependency order
		// (deps before dependents). Registration order in All breaks ties so runs are deterministic.
		public static IReadOnlyList<IImportModule> Plan(IEnumerable<IImportModule> requested)
		{
			// 1. Transitive closure over DependsOn.
			var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var pending = new Stack<IImportModule>(requested);
			while (pending.Count > 0)
			{
				var m = pending.Pop();
				if (!wanted.Add(m.Name))
					continue;

				foreach (var dep in m.DependsOn)
					pending.Push(Find(dep) ?? throw new InvalidOperationException($"Module '{m.Name}' depends on unknown module '{dep}'."));
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

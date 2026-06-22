using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.V2.Modules;

namespace Faolan.Importer.V2
{
	// The catalog of import modules + selection/dependency resolution. Modules are wrapped from the existing
	// CaptureX/Rdb importers incrementally (see docs/importer-v2-plan.md "Implementation steps"); this list
	// grows as each is ported. Registration order is the stable tie-break for the topo sort.
	public static class ModuleRegistry
	{
		public static IReadOnlyList<IImportModule> All { get; } =
		[
			// --- capture modules (ported so far) ---
			new ItemsModule(),
			new ShopsModule(),
			new CharactersModule(),
			// TODO(importer-v2): listings, maps, spells, abilities, starting-spells, conversations,
			// character-state, learned-spells, npcs, world-objects, world-bursts, quests, mounts.
			// --- rdb modules (opt-in, require --rdb) ---
			// TODO(importer-v2): rdb-inventory, rdb-morphs, rdb-spells, rdb-maps, rdb-lua, rdb-respawns, rdb-items.
			// --- tsv modules ---
			// TODO(importer-v2): ellatha-levels, ellatha-combos.
		];

		public static IImportModule? Find(string name) =>
			All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

		// The default set when no --only is given: every non-RDB module (RDB is opt-in - see plan "Decisions").
		public static IReadOnlyList<IImportModule> DefaultSet =>
			All.Where(m => m.Source is ImportSource.Captures or ImportSource.Tsv).ToList();

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

			foreach (var m in All.Where(m => wanted.Contains(m.Name)))
				Visit(m);

			return ordered;
		}
	}
}

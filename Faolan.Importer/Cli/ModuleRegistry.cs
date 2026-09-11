using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Importer.Modules;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     The catalog of import modules, plus selection and dependency resolution. Each module wraps a capture or RDB
	///     importer or extractor; see docs/tooling/importer.md. Registration order is the stable tie-break for the
	///     topological sort.
	/// </summary>
	/// <remarks>
	///     Capture-sourced modules each wrap a capture importer or extractor plus the shared import orchestration.
	///     RDB-sourced modules are opt-in (Source == Rdb keeps them out of the default set) and all require an RDB
	///     directory; the two with out-of-database write side effects - rdb-respawns writing mirror-store blobs and
	///     rdb-items dumping templates - suppress that write under --dry-run.
	/// </remarks>
	public static class ModuleRegistry
	{
		/// <remarks>
		///     Registered in default-run order so a no-flag run sequences the same every time; the topological sort is
		///     stable by this order. The RDB modules and mounts are opt-in (InDefaultSet is false).
		/// </remarks>
		public static IReadOnlyList<IImportModule> All { get; } =
		[
			// --- capture modules ---
			new ListingsModule(), new StockPhrasesModule(), new ItemsModule(), new ChatItemRefModule(), new QuestRewardItemModule(), new MapsModule(), new SpellsModule(), new StatDamageModule(), new SpellEffectsModule(), new AbilitiesModule(),
			new StartingSpellsModule(),
			new CharactersModule(), new ConversationsModule(), new CharacterStateModule(), new LearnedSpellsModule(), new ClassSpellsFromKnownModule(), new CharacterAbilitiesModule(), new CharacterQuestsModule(),
			new CharacterBankModule(), new CharacterSocketsModule(), new ShopsModule(), new NpcsModule(), new NpcPlacementsModule(), new NpcDispositionModule(), new NpcTypeAppearanceModule(), new NpcScriptEventsModule(), new NpcComboClassModule(), new NpcDeathBehaviorsModule(), new NpcAmbientAnimationsModule(), new NpcFollowPairsModule(), new NpcChatterModule(), new WorldObjectsModule(),
			new WorldTeleportsModule(), new MapLootModule(), new QuestsModule(), new QuestWiringModule(), new QuestRewardsModule(), new TravelWiringModule(), new MountsModule(), // opt-in
			new PetsModule(), // opt-in (#307)
			new ConsumableUsesModule(), // consumable on-use spells -> Items.OnUseSpell
			new ClaimsModule(), // opt-in (account claims -> AccountClaim)
			new RecipesModule(), // opt-in (crafting recipe book)
			// --- tsv modules ---
			// There are no web-scrape modules: the RDB carries both facts they would stand in for - a spell's class
			// membership (the class-encoded GuiStackingLine, RdbSpellImporter) and a combo's reuse time (the combo
			// XML's own `cooldown`, RdbComboImporter).
			new StarterSpellLevelsModule(), // capture-sourced, and the create kit is a LIST, not a level query
			// --- rdb modules (opt-in, require --rdb) ---
			new RdbInventoryModule(), new RdbMorphsModule(), new RdbAppearanceModule(), new RdbSpellsModule(), new RdbLdbNamesModule(), new RdbAchievementsModule(), new RdbFactionsModule(), new RdbLdbAbilitiesModule(), new RdbAbilitiesModule(), new RdbCombosModule(), new RdbFeatsModule(),
			new RdbMapsModule(), new RdbNameFilterModule(), new RdbRespawnsModule(), new RdbQuestCatalogModule(), new RdbItemsModule(), new RdbNpcSpawnsModule(), new RdbQuestTextModule(), new RdbDialogueTeleportsModule(), new RdbRegionsModule(), new ClassAttributeBaseModule(), new ClassComboScalingModule(),
			// --- corrections that must outrank an rdb module ---
			// Last on purpose: rdb-spells writes Spells.DurationMs unconditionally, so a capture-sourced duration for
			// the rows its property run leaves empty has to be applied after it. Registration order is the topological
			// sort's tie-break, so this placement is the ordering.
			new BuffDurationsModule()
		];

		/// <summary>
		///     The default set when no --only is given: every module that opts in.
		/// </summary>
		public static IReadOnlyList<IImportModule> DefaultSet => All.Where(m => m.InDefaultSet).ToList();

		/// <summary>
		///     Resolves a module by its CLI name ("items", from --only / --skip) or by its class name
		///     (<c>nameof(ItemsModule)</c>, the compile-checked key DependsOn uses so a renamed or removed module
		///     breaks the build rather than a run).
		/// </summary>
		public static IImportModule? Find(string name)
		{
			return All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.GetType().Name, name, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		///     Expands a requested set with its transitive dependencies and returns it in dependency order,
		///     dependencies before dependents. Registration order in <see cref="All" /> breaks ties, so runs are
		///     deterministic.
		/// </summary>
		public static IReadOnlyList<IImportModule> Plan(IEnumerable<IImportModule> requested)
		{
			// 1. Transitive closure over DependsOn (what a module needs) and RequiredAfter (what must happen because
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

			// 2. Topological sort (depth-first), iterating All so the output order is stable.
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

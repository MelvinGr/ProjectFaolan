using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// The hand-authored quest WIRING overlay (#358): the server-trigger fields (GoalType/TargetName/QuestItemId), chain
	// successors (NextQuestId) and byte-exact @609/snapshot detail that captures don't carry - the Casilda + Shipwrecked
	// chains and the sourced location quests. A distinct module (not baked into `quests`) so it's selectable/skippable and
	// keeps the definition harvest pure. Runs AFTER quests (DependsOn) so it overlays last; trigger/chain seeds are
	// BOOTSTRAP-ONLY, so a re-run never reverts a Quest-editor edit (only wire fields refresh from the capture).
	public sealed class QuestWiringModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(QuestsModule), nameof(MapsModule), nameof(WorldObjectsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Hand-authored quest wiring (trigger fields + chains + @609 detail) onto the harvested definitions.";
		public string Name => "quest-wiring";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			await QuestGoalWiring.WireQuests(ctx.Db);

			// Only quests whose objective region provably rebuilds may ride the login snapshot. Verified HERE, as the
			// last thing that touches these rows: `quests` refreshes the goal fields from the captures and this overlay
			// then re-applies the hand-authored ones, so a check run before it judges bytes we never ship.
			var (verified, verifiable) = await ImportOps.VerifyObjectiveRegions(ctx.Db,
				ObjectiveGoalExtractor.CapturedRegions(ctx.Captures.SelectMany(c => c.Packets).ToList()));
			Console.WriteLine($"  objective regions verified byte-exact: {verified} of {verifiable} quests.");

			// AFTER the verification, which is the gate it reads.
			var seeded = await ImportOps.SeedDerivedSnapshotRecords(ctx.Db);
			Console.WriteLine($"  derived a Quest_c record for {seeded} quest(s) that had everything but one.");

			return new ImportResult($"quest wiring applied (Casilda + Shipwrecked chains + sourced location quests); {seeded} derived snapshot record(s).");
		}
	}
}

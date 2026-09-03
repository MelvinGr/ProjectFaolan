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
	// Per-character quest JOURNAL (CharacterQuest) recovered from the capture's s2c QuestIIR/QuestGoal packets, so an
	// imported character carries its active/completed quests + goal progress. Needs characters (attribution) and the
	// quest definitions (the journal references them).
	public sealed class CharacterQuestsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(QuestsModule), nameof(MapsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character quest journals (QuestIIR/QuestGoal).";
		public string Name => "character-quests";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (chars, quests) = await CaptureQuestImporter.ImportAsync(ctx.Db, ctx.RawPackets);
			return new ImportResult($"{chars} characters, {quests} quest rows.");
		}
	}
}

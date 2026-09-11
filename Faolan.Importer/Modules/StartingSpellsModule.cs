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
	/// <summary>
	///     Per-class starting spells, from the create captures' 0x200A grants.
	/// </summary>
	public sealed class StartingSpellsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-class spell access map (create 0x200A grants).";
		public string Name => "class-spells";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (harvested, added) = await CaptureSpellImporter.ImportClassSpellsAsync(ctx.Db, ctx.CreateCaptures);
			return new ImportResult($"{harvested} harvested, {added} new.");
		}
	}
}

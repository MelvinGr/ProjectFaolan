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
	///     Maps from MapChange packets, plus the tooling-suffix name sanitize backfill.
	/// </summary>
	public sealed class MapsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Maps from MapChange packets (+ name sanitize).";
		public string Name => "maps";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (imported, reflagged) = await CaptureMapImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			var renamed = 0;
			foreach (var map in await ctx.Db.Maps.ToListAsync())
			{
				var clean = RdbMapImporter.SanitizeMapName(map.Name);
				if (clean != map.Name)
				{
					map.Name = clean;
					renamed++;
				}
			}

			if (renamed > 0)
				await ctx.Db.SaveChangesAsync();

			return new ImportResult($"{imported} maps imported, {reflagged} PlayfieldFlags updated, {renamed} names sanitized (#31).");
		}
	}
}

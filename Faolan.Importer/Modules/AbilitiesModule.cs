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
	// Ability grants (0x2052) -> Abilities.
	//
	// ⚠️ This used to carry six (abilityId -> slot) constants rehomed from the retired archive sqlite, and apply them
	// AFTER the harvest - so for those six the constant beat the wire. It is gone (#638): the 0x2052 grant's own
	// leading word is the slot (CaptureAbilityImporter), and blanking all six and re-harvesting restores every one of
	// them to the identical value, so the table was pure duplication with the wrong precedence.
	public sealed class AbilitiesModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Ability grants (0x2052).";
		public string Name => "abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (seen, added, updated) = await CaptureAbilityImporter.ImportAsync(ctx.Db, ctx.RawPackets);

			return new ImportResult($"{seen} seen, {added} new, {updated} updated.");
		}
	}
}

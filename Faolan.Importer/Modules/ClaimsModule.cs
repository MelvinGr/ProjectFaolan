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
	///     Account claims (CoreClaimIIR) -> AccountClaim rows, attached to the import account. Opt-in like the other
	///     account-state seeds. See docs/subsystems/item-claims.md.
	/// </summary>
	/// <remarks>
	///     The item TEA keys the claims reference are harvested into Items by the items module (ItemHashHarvester's
	///     claim-list source), so this depends on `items` for the FK and the key the server re-sends on the wire.
	///     Overwrite rebuilds the account's claim set cleanly.
	/// </remarks>
	public sealed class ClaimsModule : IImportModule
	{
		public IReadOnlyList<string> DependsOn => [nameof(ItemsModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Overwrite
		};

		public ImportSource Source => ImportSource.Captures;
		public bool InDefaultSet => false;

		public string Description => "Account claims (CoreClaimIIR list) -> AccountClaim (opt-in).";
		public string Name => "claims";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var account = await ctx.Db.Accounts.FirstOrDefaultAsync(a => a.UserName == CaptureCharacterImporter.ImportAccountName);
			if (account == null)
				return new ImportResult($"no '{CaptureCharacterImporter.ImportAccountName}' account - run the characters module first; skipped.");

			var (parsed, written) = await CaptureClaimImporter.ImportAsync(ctx.Db, ctx.RawPackets, account.Id);
			return new ImportResult($"{parsed} claims parsed, {written} written to account '{CaptureCharacterImporter.ImportAccountName}'.");
		}
	}
}

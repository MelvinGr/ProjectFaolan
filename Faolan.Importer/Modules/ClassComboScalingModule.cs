using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Rdb;
using Faolan.Importer.Cli;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	/// <summary>
	///     Fills <see cref="ClassComboScaling" /> from the client's `RDBComboLookup_c` data table (RDB type 1000513).
	/// </summary>
	/// <remarks>
	///     One of the client's named CoreTables data tables (docs/rdb/client-data-tables.md), and unlike the item/feat
	///     templates it is not TEA-encrypted - it is a flat header plus fixed-stride rows, so nothing here has to
	///     search for a signature. The table is a single RDB entry (id 0) holding all 960 rows.
	///     <para>
	///         The layout is read off the client's own parser (<c>FUN_00846580</c>): a 16-byte
	///         <c>[type][id][version][count]</c> header, then <c>count</c> rows of six little-endian u32. The client
	///         reads all six but keeps five - the third word is consumed and discarded, and it is zero in every row -
	///         and turns words 4 and 5 into floats by dividing them by 100. The surviving five become a
	///         <c>std::map</c> keyed on <c>(class, level)</c>.
	///     </para>
	///     <para>
	///         The table is its own acceptance test: it must be exactly 12 classes x 80 contiguous levels, every class
	///         id must be a <see cref="ConanClass" />, and the discarded column must really be zero everywhere. A row
	///         that breaks any of those means the stride is wrong, so the import fails rather than writing it.
	///         <see cref="Parse" /> holds the LAYOUT half (so it can be tested on a two-row blob) and
	///         <see cref="RunAsync" /> the completeness half.
	///     </para>
	/// </remarks>
	public sealed class ClassComboScalingModule : IImportModule
	{
		/// <summary>The client's `RDBComboLookup_c` RDB type. Its only entry is id 0.</summary>
		public const uint ComboLookupType = 1000513;

		private const int HeaderBytes = 16;
		private const int RowBytes = 24;
		public const uint MaxLevel = 80;

		public IReadOnlyList<string> DependsOn => [];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode> { WriteMode.Insert, WriteMode.Upsert, WriteMode.Overwrite };

		public ImportSource Source => ImportSource.Rdb;

		public string Description => "Per-class/level combo DPS + stamina scaling from the client's RDBComboLookup_c table (RDB 1000513).";
		public string Name => "rdb-combo-scaling";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			using var rdb = new RdbReader(ctx.RdbDir!, ctx.RdbProxyBase);
			var entry = rdb.EntriesOfType(ComboLookupType).Where(rdb.CanRead).ToList();
			if (entry.Count != 1)
				throw new InvalidOperationException($"RDB {ComboLookupType} (RDBComboLookup_c): expected exactly one readable entry, found {entry.Count}.");

			var rows = Parse(rdb.ReadDecompressed(entry[0]));

			// The completeness check is on the TABLE, not on the layout (which is Parse's job): the client looks a row
			// up for every class at every level, so anything short of 12 x 80 is a defect wherever it came from.
			foreach (var g in rows.GroupBy(r => r.Class))
			{
				var levels = g.Select(r => r.Level).OrderBy(l => l).ToList();
				if (!levels.SequenceEqual(Enumerable.Range(1, (int)MaxLevel).Select(l => (uint)l)))
					throw new InvalidOperationException($"RDB {ComboLookupType}: {g.Key} has {levels.Count} levels, not a contiguous 1..{MaxLevel}.");
			}

			var missing = Enum.GetValues<ConanClass>().Except(rows.Select(r => r.Class)).ToList();
			if (missing.Count > 0)
				throw new InvalidOperationException($"RDB {ComboLookupType}: no rows for {string.Join(", ", missing)}.");

			// Delete + reinsert: the natural (Class, Level) key makes this a clean rebuild with no id churn.
			await ctx.Db.ClassComboScalings.ExecuteDeleteAsync();
			ctx.Db.ClassComboScalings.AddRange(rows);
			await ctx.Db.SaveChangesAsync();

			foreach (var g in rows.GroupBy(r => r.Class).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
				ctx.Out.WriteLine($"  {g.Key,-16} {g.Count(),3} levels, dps {g.Min(r => r.DpsModifier)}..{g.Max(r => r.DpsModifier)}, " +
								$"stamina divisor {g.Min(r => r.StaminaDivisor)}..{g.Max(r => r.StaminaDivisor)}");

			var silent = rows.Where(r => r.DpsModifier == 0).Select(r => r.Class).Distinct().ToList();
			return new ImportResult($"{rows.Count} combo-scaling rows over {rows.Select(r => r.Class).Distinct().Count()} classes" +
									$"; {silent.Count} carry no DPS modifier at any level ({string.Join(", ", silent.OrderBy(c => c.ToString(), StringComparer.Ordinal))})");
		}

		/// <summary>
		///     Parses the decompressed 1000513 blob into its rows, validating the shape the client's parser assumes.
		///     Public so a test can hold the layout to the numbers without an RDB present.
		/// </summary>
		public static List<ClassComboScaling> Parse(ReadOnlySpan<byte> blob)
		{
			if (blob.Length < HeaderBytes || blob.ReadU32Le(0) != ComboLookupType)
				throw new InvalidOperationException($"RDB {ComboLookupType}: not a data-table blob (got {blob.Length} B).");

			var count = blob.ReadU32Le(12);
			if (HeaderBytes + (long)count * RowBytes != blob.Length)
				throw new InvalidOperationException(
					$"RDB {ComboLookupType}: {count} rows x {RowBytes} B + {HeaderBytes} B header != the {blob.Length} B payload - the stride is wrong.");

			var rows = new List<ClassComboScaling>((int)count);
			for (var i = 0; i < count; i++)
			{
				var at = HeaderBytes + i * RowBytes;
				var cls = blob.ReadU32Le(at);
				var level = blob.ReadU32Le(at + 4);
				var discarded = blob.ReadU32Le(at + 8);

				if (!Enum.IsDefined((ConanClass)cls))
					throw new InvalidOperationException($"RDB {ComboLookupType} row {i}: {cls} is not a ConanClass.");
				if (level is 0 or > MaxLevel)
					throw new InvalidOperationException($"RDB {ComboLookupType} row {i}: level {level} is outside 1..{MaxLevel}.");
				if (discarded != 0)
					throw new InvalidOperationException($"RDB {ComboLookupType} row {i}: the word the client discards is {discarded}, not 0.");

				rows.Add(new ClassComboScaling
				{
					Class = (ConanClass)cls,
					Level = level,
					DpsModifier = blob.ReadU32Le(at + 12),
					SecondaryModifier = blob.ReadU32Le(at + 16),
					StaminaDivisor = blob.ReadU32Le(at + 20)
				});
			}

			return rows;
		}
	}
}

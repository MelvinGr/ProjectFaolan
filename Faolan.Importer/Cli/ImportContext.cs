using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Database;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     Shared, per-run state handed to every module. Captures are read once and shared; the database context is
	///     the single tracked context the runner wraps in a transaction.
	/// </summary>
	public sealed class ImportContext
	{
		public required DatabaseContext Db { get; init; }
		public IReadOnlyDictionary<string, WriteMode> ModeOverrides { get; init; } = new Dictionary<string, WriteMode>();

		/// <remarks>
		///     Per create-flow capture (its file name contains "creat"), that capture's packet bytes - the source for
		///     the starting ability and spell kits, each capture's grants attributed to the class it created.
		/// </remarks>
		public required IReadOnlyList<IReadOnlyList<byte[]>> CreateCaptures { get; init; }
		public required IReadOnlyList<NamedCapture> Captures { get; init; }

		/// <remarks>
		///     Raw packet bytes across every capture plus the loose *.bin files in the captures directory - the input
		///     the item, map, spell and ability importers scan (the <c>itemPackets</c> source).
		/// </remarks>
		public required IReadOnlyList<byte[]> RawPackets { get; init; }

		public TextWriter Out { get; init; } = Console.Out;

		/// <remarks>
		///     Global write strategy plus optional per-module overrides (<c>--mode npcs=overwrite,items=insert</c>).
		/// </remarks>
		public WriteMode Mode { get; init; } = WriteMode.Upsert;

		/// <remarks>
		///     Informational for database writes - the runner's transaction rollback is what makes a dry run write
		///     nothing - but a module with an out-of-database side effect (rdb-respawns writing CDN blobs to the mirror
		///     store, say) must check this and skip that side effect.
		/// </remarks>
		public bool DryRun { get; init; }

		/// <remarks>
		///     Permits Overwrite on guarded modules, such as characters, which cascades to live player data.
		/// </remarks>
		public bool Force { get; init; }
		/// <remarks>
		///     The corpus directory the run loaded, or empty when no module in the plan needed it (a run of Db-source
		///     modules only).
		/// </remarks>
		public required string CapturesDir { get; init; }
		public required string? RdbProxyBase { get; init; }
		public string? DumpTemplatesDir { get; init; }

		/// <remarks>
		///     Sub-options for the rdb-* modules. A module's out-of-database write paths must be suppressed under
		///     <see cref="DryRun" /> by the module itself.
		/// </remarks>
		public string? RdbDir { get; init; }

		/// <summary>
		///     The mode a module actually runs in: the requested one - a per-module override beating the global default
		///     - coerced to something the module supports.
		/// </summary>
		/// <remarks>
		///     The coercion is recorded here rather than computed by the module, because a module only declares what it
		///     supports and cannot resolve its own fallback. A module that branches on its mode must see the coerced
		///     value, or it takes the branch for a mode the runner did not give it.
		/// </remarks>
		public WriteMode ModeFor(string moduleName)
		{
			if (_effectiveModes.TryGetValue(moduleName, out var effective))
				return effective;

			return ModeOverrides.TryGetValue(moduleName, out var m)
				? m
				: Mode;
		}

		/// <summary>Called by <see cref="ImportRunner" /> once per module, before it runs.</summary>
		public void SetEffectiveMode(string moduleName, WriteMode mode)
		{
			_effectiveModes[moduleName] = mode;
		}

		private readonly Dictionary<string, WriteMode> _effectiveModes = [];
	}
}

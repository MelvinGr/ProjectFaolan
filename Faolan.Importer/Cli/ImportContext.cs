using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Cli;
using Faolan.Core.Database;
using Faolan.Importer.Captures;
using Microsoft.Extensions.Configuration;

namespace Faolan.Importer.Cli
{
	// Shared, per-run state handed to every module. Captures are read once and shared; the DB context is the
	// single tracked context the runner wraps in a transaction.
	public sealed class ImportContext
	{
		// The Faolan.Web.Content mirror's ADDRESS, for on-demand (le.idx file==0xFF) RDB resources: RdbReader fetches
		// each CDN entry's content-addressed blob through it (and the proxy store-and-forwards, so pulls are archived).
		// Passed to every importer RdbReader so CDN-only records import instead of being skipped.
		//
		// Config-derived (`IConfiguration.CdnAddress`, the same `Cdn` section the mirror itself reads), so
		// a mirror on another box is ONE setting in faolan.user.json rather than a flag on every command; `--rdb-proxy`
		// still overrides per run. The `/gameupdate/upm/rdb` route is RdbReader's, not config's.
		// See [[aoc_content_cdn_mirror]].
		public static string DefaultProxyBase(IConfiguration config)
		{
			return config.CdnPublicAddress;
		}

		public required DatabaseContext Db { get; init; }
		public IReadOnlyDictionary<string, WriteMode> ModeOverrides { get; init; } = new Dictionary<string, WriteMode>();

		// Per create-flow capture (filename contains "creat"), its packet bytes - the source for the starting
		// ability/spell kits (each capture's grants are attributed to the class it created).
		public required IReadOnlyList<IReadOnlyList<byte[]>> CreateCaptures { get; init; }
		public required IReadOnlyList<NamedCapture> Captures { get; init; }

		// Raw packet bytes across every capture PLUS the loose *.bin files in the captures dir - the input the
		// item/map/spell/ability importers scan (the `itemPackets` source).
		public required IReadOnlyList<byte[]> RawPackets { get; init; }

		public TextWriter Out { get; init; } = Console.Out;

		// Global write strategy + optional per-module overrides (--mode npcs=overwrite,items=insert).
		public WriteMode Mode { get; init; } = WriteMode.Upsert;

		// Informational - the runner's transaction rollback is what actually makes a dry run write nothing -
		// but a module with an OUT-OF-DB side effect (e.g. rdb-respawns writing CDN blobs to the mirror store) must
		// check this and skip that side effect.
		public bool DryRun { get; init; }

		// Permits Overwrite on guarded modules (e.g. characters, which cascades to live player data).
		public bool Force { get; init; }
		// The corpus directory the run loaded, or EMPTY when no module in the plan needed it (a Db-source-only run).
		public required string CapturesDir { get; init; }
		public required string RdbProxyBase { get; init; }
		public string? DumpTemplatesDir { get; init; }

		// rdb-* module sub-options (from --rdb / --dump-templates). Out-of-DB write paths (dump) must be suppressed
		// under DryRun by the module.
		public string? RdbDir { get; init; }

		// The mode a module will actually RUN in: the requested one (per-module override beats the global default),
		// COERCED to something the module supports.
		//
		// The coercion has to be recorded here rather than computed by the module, because a module only declares
		// what it supports - it cannot resolve its own fallback. Before this existed the runner printed
		// "mode 'Upsert' unsupported; using 'Overwrite'" and then ran the module with Upsert anyway, so every module
		// that branches on its mode took the wrong branch: `npcs` never performed the clean rebuild it documents,
		// and its spawn set ACCRETED across imports - exactly the failure its own comment warns Upsert would cause.
		public WriteMode ModeFor(string moduleName)
		{
			if (_effectiveModes.TryGetValue(moduleName, out var effective))
				return effective;

			return ModeOverrides.TryGetValue(moduleName, out var m)
				? m
				: Mode;
		}

		// Called by ImportRunner once per module, before it runs.
		public void SetEffectiveMode(string moduleName, WriteMode mode)
		{
			_effectiveModes[moduleName] = mode;
		}

		private readonly Dictionary<string, WriteMode> _effectiveModes = [];
	}
}

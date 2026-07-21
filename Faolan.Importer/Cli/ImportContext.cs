using System;
using System.Collections.Generic;
using System.IO;
using Faolan.Core.Database;
using Faolan.Importer.Captures;

namespace Faolan.Importer.Cli
{
	// Shared, per-run state handed to every module. Captures are read once and shared; the DB context is the
	// single tracked context the runner wraps in a transaction.
	public sealed class ImportContext
	{
		// The Faolan.Web.Content mirror-proxy base for on-demand (le.idx file==0xFF) RDB resources: RdbReader fetches
		// each CDN entry's content-addressed blob through it (and the proxy store-and-forwards, so pulls are archived).
		// Passed to every importer RdbReader so CDN-only records import instead of being skipped. Override with
		// --rdb-proxy (e.g. to point at a remote mirror); defaults to the local mirror. See [[aoc_content_cdn_mirror]].
		public const string DefaultProxyBase = "http://127.0.0.1:7081/gameupdate/upm/rdb";

		// Informational - the runner's transaction rollback is what actually makes a dry run write nothing -
		// but a module with an OUT-OF-DB side effect (e.g. rdb-respawns writing CDN blobs to --store) must
		// check this and skip that side effect.
		public bool DryRun { get; init; }

		// Permits Overwrite on guarded modules (e.g. characters, which cascades to live player data).
		public bool Force { get; init; }
		public required DatabaseContext Db { get; init; }
		public IReadOnlyDictionary<string, WriteMode> ModeOverrides { get; init; } = new Dictionary<string, WriteMode>();

		// Raw packet bytes across every capture PLUS the loose *.bin files in the captures dir - the input the
		// item/map/spell/ability importers scan (the `itemPackets` source).
		public required IReadOnlyList<byte[]> RawPackets { get; init; }

		// Per create-flow capture (filename contains "creat"), its packet bytes - the source for the starting
		// ability/spell kits (each capture's grants are attributed to the class it created).
		public required IReadOnlyList<IReadOnlyList<byte[]>> CreateCaptures { get; init; }
		public required IReadOnlyList<NamedCapture> Captures { get; init; }
		public required string CapturesDir { get; init; }
		public string RdbProxyBase { get; init; } = DefaultProxyBase;
		public string? DumpTemplatesDir { get; init; }

		// rdb-* module sub-options (from --rdb / --dump-templates). Out-of-DB write paths (dump) must be suppressed
		// under DryRun by the module.
		public string? RdbDir { get; init; }

		public TextWriter Out { get; init; } = Console.Out;

		// Global write strategy + optional per-module overrides (--mode npcs=overwrite,items=insert).
		public WriteMode Mode { get; init; } = WriteMode.Upsert;

		// The effective write mode for a module (per-module override beats the global default).
		public WriteMode ModeFor(string moduleName)
		{
			return ModeOverrides.TryGetValue(moduleName, out var m)
				? m
				: Mode;
		}
	}
}

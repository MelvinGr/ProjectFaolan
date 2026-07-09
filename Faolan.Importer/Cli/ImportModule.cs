using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Importer.Cli
{
	// Importer module scaffolding. See docs/tooling/importer-plan.md. Each importable subsystem is an IImportModule,
	// selected by Name (--only/--skip), ordered by DependsOn, and run inside one transaction so --dry-run can
	// roll the whole thing back and just print the delta.

	// What external input a module consumes. The runner gates a module out (with a notice) when its source is
	// absent (e.g. an rdb-* module without --rdb).
	public enum ImportSource
	{
		Captures,
		Rdb,
		Tsv,
		Db
	}

	// How a module's extracted rows land. See docs/tooling/importer-plan.md "Write modes".
	//   Insert    - add only rows whose key is absent; never modify/delete existing.
	//   Upsert    - update existing by key AND insert new; never delete. (default)
	//   Overwrite - replace the module's OWNED data wholesale for the keys it touches, then insert.
	public enum WriteMode
	{
		Insert,
		Upsert,
		Overwrite
	}

	// The one-line summary a module prints (mirrors today's "X seen, Y new, Z updated" lines).
	public sealed record ImportResult(string Summary);

	public interface IImportModule
	{
		// The input this module needs; absent input gates it out of a run.
		ImportSource Source { get; }

		// Module names that must run before this one (the runner resolves + topo-sorts these).
		IReadOnlyList<string> DependsOn { get; }

		// The write modes this module can honour. The runner warns + falls back to the module's first
		// supported mode if an unsupported one is requested.
		IReadOnlySet<WriteMode> SupportedModes { get; }

		// One-line description for --list and the dry-run header.
		string Description { get; }

		// CLI token (kebab-case) used by --only/--skip and shown by --list.
		string Name { get; }

		// Whether this module runs in the default (no --only) set. RDB modules are opt-in by source; a capture
		// module can also opt out (e.g. mounts, which are seeded only via --only mounts).
		bool InDefaultSet => Source is ImportSource.Captures or ImportSource.Tsv;

		Task<ImportResult> RunAsync(ImportContext ctx);
	}
}

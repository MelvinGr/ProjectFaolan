using System.Collections.Generic;
using System.Threading.Tasks;

namespace Faolan.Importer.Cli
{
	/// <summary>
	///     What external input a module consumes. The runner gates a module out, with a notice, when its source is
	///     absent - an rdb-* module without an RDB directory, say.
	/// </summary>
	/// <remarks>
	///     Importer module scaffolding; see docs/tooling/importer.md. Each importable subsystem is an
	///     <see cref="IImportModule" />, selected by name (--only / --skip), ordered by its dependencies, and run
	///     inside one transaction so --dry-run can roll the whole thing back and print the delta.
	/// </remarks>
	public enum ImportSource
	{
		Captures,
		Rdb,
		Tsv,
		Db
	}

	/// <summary>
	///     How a module's extracted rows land. See docs/tooling/importer.md, "Write modes".
	/// </summary>
	/// <remarks>
	///     Insert adds only rows whose key is absent and never modifies or deletes an existing one; Upsert, the
	///     default, updates existing rows by key and inserts new ones but never deletes; Overwrite replaces the
	///     module's owned data wholesale for the keys it touches, then inserts.
	/// </remarks>
	public enum WriteMode
	{
		Insert,
		Upsert,
		Overwrite
	}

	/// <summary>The one-line summary a module prints: "X seen, Y new, Z updated".</summary>
	public sealed record ImportResult(string Summary);

	public interface IImportModule
	{
		/// <summary>Module names that must run before this one; the runner resolves and topologically sorts them.</summary>
		IReadOnlyList<string> DependsOn { get; }

		/// <summary>
		///     Module names that must run after this one, because this module alone leaves the database in a state
		///     nobody should stop at. Defaults to empty; only a module with that hazard declares one.
		/// </summary>
		/// <remarks>
		///     The prerequisite edge cannot express this: <c>--only X</c> pulls in what X needs, not what needs to
		///     happen because X ran. The case it exists for is <c>quests</c>, which refreshes the harvested goal fields
		///     from the captures and so clobbers the hand-authored trigger values <c>quest-wiring</c> owns.
		/// </remarks>
		IReadOnlyList<string> RequiredAfter => [];

		/// <summary>
		///     The write modes this module can honour. The runner warns and falls back to the module's first supported
		///     mode when an unsupported one is requested.
		/// </summary>
		IReadOnlySet<WriteMode> SupportedModes { get; }

		/// <summary>The input this module needs; absent input gates it out of a run.</summary>
		ImportSource Source { get; }

		/// <summary>One-line description for --list and the dry-run header.</summary>
		string Description { get; }

		/// <summary>CLI token (kebab-case) used by --only and --skip, and shown by --list.</summary>
		string Name { get; }

		/// <summary>
		///     Whether this module runs in the default set, i.e. with no --only. RDB modules are opt-in by source, and
		///     a capture module can opt out as well - mounts is seeded only through <c>--only mounts</c>.
		/// </summary>
		bool InDefaultSet => Source is ImportSource.Captures or ImportSource.Tsv;

		Task<ImportResult> RunAsync(ImportContext ctx);
	}
}

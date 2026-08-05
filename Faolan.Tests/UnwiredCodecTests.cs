using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Which wire-proven codecs have NO live trigger - a builder that is byte-exact-locked by a golden but that no
	// server code ever calls (#405).
	//
	// ⚠️ THIS EXISTS BECAUSE THE HAND-WRITTEN VERSION ROTTED. #405 carried the list as issue prose, and the #530
	// codec sweep renamed or dissolved every single name on it - `CreateEditorRecordBuilder.BuildComboBookIir`,
	// `MoveTaskCodec.BuildApproachTargetBody`, `SpellRecastActionBuilder.BuildChangeRecastTimeBody` and the rest all
	// stopped existing, and nothing noticed, because prose has no compiler. The list has to be DERIVED to be worth
	// having, so it is derived here and the allowlist below carries the reasons.
	//
	// The gate is two-sided on purpose:
	//   * a codec that becomes unwired without a documented reason FAILS - that is a builder quietly losing its
	//     caller, which is the bug this is really watching for;
	//   * an allowlisted codec that GAINS a caller also fails - the entry is now a lie and must come off, which is
	//     what stops the list rotting the way the prose one did.
	public class UnwiredCodecTests
	{
		// A codec is UNWIRED when nothing outside `Codecs/` and the test project names it. Each entry needs a reason,
		// and the reason is the point: most of these are correct, not gaps.
		private static readonly Dictionary<string, string> Unwired = new(StringComparer.Ordinal)
		{
			["SpellBookCodec"] = "the spell book rides the login snapshot instead, so the standalone message has no caller",
			["AreaFormulaCodec"] = "area/cone target selection - unblocked by Spell.AreaRadius (#349) but the selection itself is not implemented",
			["CrimeCodec"] = "blocked on the criminal system itself (#503)",
			["KnubotStartTradeCodec"] = "the NPC trade window is not implemented",
			["KnubotFinishTradeCodec"] = "the NPC trade window is not implemented",
			["ClientAddGuildRenownCodec"] = "guild renown is never awarded, so nothing calls it",
			["CharacterActionCodec"] = "a create-editor record the live create flow omits; the client accepts creation without it"
		};

		// LockedToChanged came OFF the list in the same pass that wrote it (#405): retail sends it in the NPC-aggro
		// bundle, right after HateListUpdate/ThreatLevel, and we now send all three. This is the gate doing its job in
		// the direction that matters - the list said "no live trigger yet", and looking for the trigger found it.
		[Fact]
		public void EveryUnwiredCodec_IsDocumented_AndEveryDocumentedOneIsStillUnwired()
		{
			var callers = CallerSources();
			var standalone = StandaloneMessageCodecs().ToList();
			Assert.True(standalone.Count > 100, $"only {standalone.Count} standalone-message codecs found - the enumeration is broken, not the codebase");

			var unwired = standalone.Where(t => !callers.Any(src => src.Contains($"{t.Name}.", StringComparison.Ordinal))).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

			var undocumented = unwired.Where(n => !Unwired.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
			var stale = Unwired.Keys.Where(n => !unwired.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

			Assert.True(undocumented.Count == 0,
				"these codecs have no live caller and no recorded reason - either wire them or add a line to Unwired saying why not (#405):\n  " + string.Join("\n  ", undocumented));

			Assert.True(stale.Count == 0,
				"these are listed as unwired but now HAVE a caller - take them off the list, it is meant to rot loudly rather than silently (#405):\n  " + string.Join("\n  ", stale));
		}

		// Every codec that declares a standalone message: an IIR opcode, an IIR family, or an RPC method. Sub-record
		// codecs (composed inside a larger body, never sent alone) declare nothing and are not in scope - having no
		// caller of their own is what they ARE.
		private static IEnumerable<Type> StandaloneMessageCodecs()
		{
			return typeof(Endpoints).Assembly.GetTypes()
				.Where(t => t is { IsAbstract: true, IsSealed: true, Namespace: not null } && t.Namespace.Contains(".Codecs", StringComparison.Ordinal))
				.Where(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f => f.Name is "Opcode" or "HandledTypes" or "Method" or "HandledMethods"));
		}

		// Everything that could legitimately CALL a codec: the server, the importer, the web tier and the MCP server.
		// `Codecs/` itself is excluded (a codec composing another is not a live trigger) and so is the test project
		// (a golden proving a builder is exactly the situation this looks for).
		private static IReadOnlyList<string> CallerSources()
		{
			string[] roots =
			[
				TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Core"), TestPaths.Repo("Faolan.Core.Database"),
				TestPaths.Repo("Faolan.Importer"), TestPaths.Repo("Faolan.Mcp"), TestPaths.Repo("Faolan.Web"),
				TestPaths.Repo("Faolan.ClientLauncher")
			];

			var codecs = TestPaths.Repo("Faolan.Core.Network", "Codecs") + Path.DirectorySeparatorChar;

			return
			[
				.. roots.Where(Directory.Exists)
					.SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
					.Where(f => !f.StartsWith(codecs, StringComparison.Ordinal))
					.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
					            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					.Select(File.ReadAllText)
			];
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.GameServer;
using Faolan.GameServer.Gm;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The per-command GM privilege gate: each verb in the command table carries the gm_level bit it
	// requires, and the ten verbs that are retail verbs must carry retail's own mask - read live from the recovered
	// registry (docs/re/gm-command-registry.tsv) rather than restated here, so a registry correction propagates.
	public class GmCommandTableTests
	{
		// Every verb of ours that retail's client also registers. The mask for these is a recovered fact; the rest
		// of GmCommandMasks is our own family choice and is deliberately not pinned (changing a choice is not a
		// regression).
		//
		// `time` is deliberately not here. Retail's `/time` manipulates frame times (the deltatimer system) - the
		// time-of-day setter is `/settime`, which is what our `.time` does. We do not implement retail's `/time`,
		// so pinning its mask would assert against a verb we do not have.
		private static readonly string[] RetailVerbs =
		[
			"save", "tradeskill", "teleport", "teleportself", "tp", "knockback", "feat", "level", "kill",
			"settime", "gametime", "distance", "whereis", "playfieldinfo", "playfieldinstancelock", "ghost", "system", "hatedbylist",
			"summon", "unsummon", "assist", "falldamage", "cash",
			"inflict", "areaknockback", "resurrect",
			// The stat family; `setstat` lives under `setlocal`.
			"get", "set", "setlocal", "getskill", "teleporttarget",
			// The broadcast family, and monster visibility under `mobs`.
			"broadcast", "servcast", "sitecast", "mobs",
			// `feats` is the manipulator; `feat` is the reader.
			"feats", "char", "kickban",
			// Item removal lives under `inventory delete`.
			"getfaction", "setfaction", "magic", "inventory",
			"gethash", "quest",
			"settimescale", "showclassstatadjustments", "update"
		];

		private static Dictionary<string, uint> RegistryMasks()
		{
			return File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-registry.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t'))
				.ToDictionary(f => f[0].TrimStart('/'), f => Convert.ToUInt32(f[1], 16), StringComparer.OrdinalIgnoreCase);
		}

		[Fact]
		public void RetailVerbs_CarryRetailsOwnMask()
		{
			var registry = RegistryMasks();
			foreach (var verb in RetailVerbs)
			{
				Assert.True(registry.ContainsKey(verb), $"'{verb}' is not in the recovered registry - it is not a retail verb.");
				Assert.True(GmCommandService.GmCommandMasks.TryGetValue(verb, out var mask), $"'{verb}' has no mask entry.");
				Assert.Equal(registry[verb], (uint)mask);
			}
		}

		// None = unrestricted is the client's own convention for an unmasked command. Exactly two verbs are unmasked:
		// our `.help` (it filters its listing per caller instead) and `/system`, which retail itself registers at mask
		// 0x0 - so leaving it unmasked is matching retail, not an oversight, and `RetailVerbs_CarryRetailsOwnMask`
		// independently pins it to the registry's own value. Everything else must carry a bit: an accidentally-None
		// entry would silently open that command to every GM.
		[Fact]
		public void OnlyHelpAndSystem_AreUnrestricted()
		{
			var unmasked = GmCommandService.GmCommandMasks.Where(kv => kv.Value == GmPrivileges.None).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
			Assert.Equal(["help", "system"], unmasked);
		}

		// The join refuses to come up on any asymmetry between the handler table and the mask table - a handler
		// with no mask would otherwise dispatch ungated.
		// The completeness gate: Every verb in the client's registry must be answerable - either we implement it or
		// the catalog carries it with retail's mask and usage. A verb in neither comes back as "no such command",
		// which reads as a broken build rather than an absent subsystem.
		[Fact]
		public void EveryRetailVerb_IsEitherImplementedOrCatalogued()
		{
			var registry = File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-registry.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t')[0].TrimStart('/'))
				.ToList();

			var answered = GmCommandService.GmCommandMasks.Keys
				.Concat(RetailCommandCatalog.Verbs.Select(u => u.Verb))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var unanswered = registry.Where(v => !answered.Contains(v)).ToList();
			Assert.True(unanswered.Count == 0, $"Retail verbs with neither an implementation nor a catalog entry: [{string.Join(", ", unanswered)}]");
		}

		// A catalogued verb carries retail's own mask, exactly as an implemented one does - the reply is a
		// placeholder, the gating is not.
		[Fact]
		public void CataloguedVerbs_CarryRetailsOwnMask()
		{
			var registry = RegistryMasks();
			foreach (var unbacked in RetailCommandCatalog.Verbs)
			{
				Assert.True(registry.TryGetValue(unbacked.Verb, out var mask), $"'{unbacked.Verb}' is catalogued but is not a retail verb.");
				Assert.Equal(mask, (uint)unbacked.Mask);
				Assert.False(string.IsNullOrWhiteSpace(unbacked.Needs), $"'{unbacked.Verb}' says nothing about what it needs.");
			}
		}

		// The transcription gate. The catalog's Usage and Description are copies of a recovered RE artifact, and
		// the risk they carry is drift: someone "tidies" a line that looks broken and the catalog quietly stops
		// describing the client. So both are pinned to docs/re/gm-command-args.tsv.
		// Deliberately not a bracket-balance check. Eight of retail's own usage lines are unbalanced - two
		// because the closing bracket is inside Funcom's help string ("Literal: Clears 1 death effect]") - so
		// asserting balance would fail on a faithful recovery and reward corrupting it. See RetailCommandCatalog.
		[Fact]
		public void CataloguedVerbs_MatchTheRecoveredGrammar()
		{
			var grammar = File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-args.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t'))
				.Where(f => f.Length >= 9)
				.ToDictionary(f => f[0].TrimStart('/'), f => (Usage: f[6], Short: f[7], Long: f[8]), StringComparer.OrdinalIgnoreCase);

			foreach (var unbacked in RetailCommandCatalog.Verbs)
			{
				Assert.True(grammar.TryGetValue(unbacked.Verb, out var recovered), $"'{unbacked.Verb}' is catalogued but has no recovered grammar.");
				Assert.Equal(recovered.Usage, unbacked.Usage);

				// The client's own description, verbatim - falling back to the long form for the one class that
				// declares no short one (/lua). The tsv cannot hold a newline, so compare it flattened the way
				// DumpCommandArgs.flatten() does: /tracescripts' description really is two lines.
				var expected = recovered.Short.Length > 0 ? recovered.Short : recovered.Long;
				Assert.Equal(expected, Flatten(unbacked.Description));
			}
		}

		private static string Flatten(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[\u0000-\u001f]+", " ").Trim();

		// A verb cannot be implemented and catalogued - the join refuses to come up on it, because whichever wins
		// would depend on insertion order.
		[Fact]
		public void NoVerb_IsBothImplementedAndCatalogued()
		{
			var both = RetailCommandCatalog.Verbs.Select(u => u.Verb).Where(GmCommandService.GmCommandMasks.ContainsKey).ToList();
			Assert.True(both.Count == 0, $"Implemented and still catalogued: [{string.Join(", ", both)}]");
		}

		[Fact]
		public void BuildGmCommands_RefusesAnAsymmetricTable()
		{
			var handlers = new Dictionary<string, GmCommandService.GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["kill"] = (_, _) => Task.CompletedTask,
				["notacommand"] = (_, _) => Task.CompletedTask
			};

			var e = Assert.Throws<InvalidOperationException>(() => GmCommandService.BuildGmCommands(handlers));
			Assert.Contains("notacommand", e.Message); // a handler with no mask
			Assert.Contains("help", e.Message); // a mask with no handler (all the rest)
		}

		[Fact]
		public void BuildGmCommands_JoinsMaskToHandler()
		{
			var handlers = GmCommandService.GmCommandMasks.Keys
				.ToDictionary(v => v, GmCommandService.GmCommandDelegate (_) => (_, _) => Task.CompletedTask, StringComparer.OrdinalIgnoreCase);

			var table = GmCommandService.BuildGmCommands(handlers);

			// Our verbs plus every retail verb we do not back yet - the catalog is folded into the same table so the
			// client's whole registry answers.
			Assert.Equal(GmCommandService.GmCommandMasks.Count + RetailCommandCatalog.Verbs.Count, table.Count);
			Assert.Equal(GmPrivileges.NpcCommands, table["kill"].RequiredMask);
			Assert.Equal(GmPrivileges.NpcCommands, table["KILL"].RequiredMask); // case-insensitive, like the chat parser
		}
	}
}

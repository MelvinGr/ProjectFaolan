using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.GameServer;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The per-command GM privilege gate (#904): each verb in the command table carries the gm_level bit it
	// requires, and the ten verbs that ARE retail verbs must carry retail's own mask - read live from the recovered
	// registry (docs/re/gm-command-registry.tsv) rather than restated here, so a registry correction propagates.
	public class GmCommandTableTests
	{
		// Every verb of ours that retail's client also registers. The mask for these is a recovered fact; the rest
		// of GmCommandMasks is our own family choice and is deliberately NOT pinned (changing a choice is not a
		// regression).
		// ⚠️ `time` is deliberately NOT here. Retail's `/time` manipulates FRAME times (the deltatimer system) - the
		// time-of-day setter is `/settime`, which is what our `.time` actually did, so it was renamed (#914 rule 1).
		// We do not implement retail's `/time`, so pinning its mask would assert against a verb we do not have.
		private static readonly string[] RetailVerbs =
		[
			"save", "tradeskill", "teleport", "teleportself", "tp", "knockback", "feat", "level", "kill",
			// #914 Wave A
			"settime", "gametime", "distance", "whereis", "playfieldinfo", "playfieldinstancelock", "ghost", "system", "hatedbylist",
			// #914 Wave B1
			"summon", "unsummon", "assist", "falldamage", "cash",
			// #914 Wave B2
			"inflict", "areaknockback", "resurrect",
			// #914 Wave B3 - the stat family (`setstat` retired into `setlocal`)
			"get", "set", "setlocal", "getskill", "teleporttarget",
			// #914 Wave B4 - broadcast family and monster visibility (`invisible` retired into `mobs`)
			"broadcast", "servcast", "sitecast", "mobs"
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
				Assert.True(GameServerListener.GmCommandMasks.TryGetValue(verb, out var mask), $"'{verb}' has no mask entry.");
				Assert.Equal(registry[verb], (uint)mask);
			}
		}

		// None = unrestricted is the client's own convention for an unmasked command. Exactly two verbs are unmasked:
		// our `.help` (it filters its listing per caller instead) and `/system`, which RETAIL itself registers at mask
		// 0x0 - so leaving it unmasked is matching retail, not an oversight, and `RetailVerbs_CarryRetailsOwnMask`
		// independently pins it to the registry's own value. Everything else must carry a bit: an accidentally-None
		// entry would silently open that command to every GM.
		[Fact]
		public void OnlyHelpAndSystem_AreUnrestricted()
		{
			var unmasked = GameServerListener.GmCommandMasks.Where(kv => kv.Value == GmPrivileges.None).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
			Assert.Equal(["help", "system"], unmasked);
		}

		// The join refuses to come up on any asymmetry between the handler table and the mask table - a handler
		// with no mask would otherwise dispatch ungated.
		// ⭐ The completeness gate: EVERY verb in the client's registry must be answerable - either we implement it or
		// the catalog carries it with retail's mask and usage. A verb in neither comes back as "no such command",
		// which reads as a broken build rather than an absent subsystem.
		[Fact]
		public void EveryRetailVerb_IsEitherImplementedOrCatalogued()
		{
			var registry = File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-registry.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t')[0].TrimStart('/'))
				.ToList();

			var answered = GameServerListener.GmCommandMasks.Keys
				.Concat(RetailCommandCatalog.Verbs.Select(u => u.Verb))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			var unanswered = registry.Where(v => !answered.Contains(v)).ToList();
			Assert.True(unanswered.Count == 0, $"Retail verbs with neither an implementation nor a catalog entry: [{string.Join(", ", unanswered)}]");
		}

		// A catalogued verb carries retail's OWN mask, exactly as an implemented one does - the reply is a
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

		// A verb cannot be implemented AND catalogued - the join refuses to come up on it, because whichever wins
		// would depend on insertion order.
		[Fact]
		public void NoVerb_IsBothImplementedAndCatalogued()
		{
			var both = RetailCommandCatalog.Verbs.Select(u => u.Verb).Where(GameServerListener.GmCommandMasks.ContainsKey).ToList();
			Assert.True(both.Count == 0, $"Implemented and still catalogued: [{string.Join(", ", both)}]");
		}

		[Fact]
		public void BuildGmCommands_RefusesAnAsymmetricTable()
		{
			var handlers = new Dictionary<string, GameServerListener.GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["kill"] = (_, _) => Task.CompletedTask,
				["notacommand"] = (_, _) => Task.CompletedTask
			};

			var e = Assert.Throws<InvalidOperationException>(() => GameServerListener.BuildGmCommands(handlers));
			Assert.Contains("notacommand", e.Message); // a handler with no mask
			Assert.Contains("help", e.Message); // a mask with no handler (all the rest)
		}

		[Fact]
		public void BuildGmCommands_JoinsMaskToHandler()
		{
			var handlers = GameServerListener.GmCommandMasks.Keys
				.ToDictionary(v => v, GameServerListener.GmCommandDelegate (_) => (_, _) => Task.CompletedTask, StringComparer.OrdinalIgnoreCase);

			var table = GameServerListener.BuildGmCommands(handlers);

			// Our verbs PLUS every retail verb we do not back yet - the catalog is folded into the same table so the
			// client's whole registry answers (#914).
			Assert.Equal(GameServerListener.GmCommandMasks.Count + RetailCommandCatalog.Verbs.Count, table.Count);
			Assert.Equal(GmPrivileges.NpcCommands, table["kill"].RequiredMask);
			Assert.Equal(GmPrivileges.NpcCommands, table["KILL"].RequiredMask); // case-insensitive, like the chat parser
		}
	}
}

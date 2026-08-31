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
		private static readonly string[] RetailVerbs =
			["save", "time", "tradeskill", "teleport", "teleportself", "tp", "knockback", "feat", "level", "kill"];

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

		// None = unrestricted is the client's own convention for an unmasked command, and `help` is the one verb we
		// deliberately leave unmasked (it filters its listing per caller instead). Everything else must carry a bit:
		// an accidentally-None entry would silently open that command to every GM.
		[Fact]
		public void OnlyHelp_IsUnrestricted()
		{
			var unmasked = GameServerListener.GmCommandMasks.Where(kv => kv.Value == GmPrivileges.None).Select(kv => kv.Key).ToList();
			Assert.Equal(["help"], unmasked);
		}

		// The join refuses to come up on any asymmetry between the handler table and the mask table - a handler
		// with no mask would otherwise dispatch ungated.
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

			Assert.Equal(GameServerListener.GmCommandMasks.Count, table.Count);
			Assert.Equal(GmPrivileges.NpcCommands, table["kill"].RequiredMask);
			Assert.Equal(GmPrivileges.NpcCommands, table["KILL"].RequiredMask); // case-insensitive, like the chat parser
		}
	}
}

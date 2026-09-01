using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The ARGUMENT GRAMMAR half of the recovered command registry (#913).
	//
	// GmCommandTableTests pins the MASK dimension of every verb we share with retail. Nothing pinned the argument
	// dimension - and that is the one that broke: `/teleportself` had the right name and the right mask (0x4) and
	// still refused the client's own map UI, because we parsed its argument as a map name while retail declares it
	// as `posx posz`. These assertions pin the recovered facts our parser is built on, so a regenerated dump that
	// disagrees fails here instead of waiting for someone to right-click a map.
	//
	// Source: docs/re/gm-command-args.tsv, produced by tools/ghidra/DumpCommandArgs.java.
	public class GmCommandArgumentGrammarTests
	{
		private sealed record CommandArgs(string Verb, uint Mask, string Class, uint Mode, string Status, string Usage, string Params);

		private static List<CommandArgs> ArgTable()
		{
			return File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-args.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t'))
				.Select(f => new CommandArgs(f[0].TrimStart('/'), Convert.ToUInt32(f[1], 16), f[2], uint.Parse(f[4]), f[5], f[6], f[9]))
				.ToList();
		}

		// The parameter NAMES of one verb, in declaration order. The params column is `name: help` pairs, and a help
		// text can itself contain a colon ("Literal: Reloads all npc-related data: RDB-data, ...") - so the name is
		// what precedes the FIRST colon, never a naive split on every one.
		private static string[] ParamNames(CommandArgs c)
		{
			return c.Params.Length == 0
				? []
				: c.Params.Split(" | ").Select(p => p.Split(':', 2)[0].Trim()).ToArray();
		}

		// ⭐ The regression this ticket is about. Retail's teleport family takes X and Z as its two mandatory
		// coordinates, with Y OPTIONAL behind a literal `y` token - which is why the client's map sends two numbers
		// and no height. If this ever reads (posx, posy) again, our two-token parse is wrong.
		[Fact]
		public void TeleportFamily_TakesXAndZFirst_WithYOptional()
		{
			var table = ArgTable();
			foreach (var verb in new[] { "tp", "teleport", "teleportself", "teleporttarget" })
			{
				var cmd = table.SingleOrDefault(c => c.Verb == verb);
				Assert.NotNull(cmd);
				Assert.Equal("FanaticTeleportCommand_c", cmd.Class); // one class serves all four verbs

				var names = ParamNames(cmd);
				Assert.Equal(["posx", "posz"], names.Take(2));
				Assert.Equal("posx posz [[y posy][playfield]] [proxy model type group subgroup running]", cmd.Usage);

				// Y is present but bracketed - never among the leading positional pair.
				Assert.Contains("posy", names);
				Assert.DoesNotContain("posy", names.Take(2));
			}
		}

		// ⭐ The registry record is SIXTEEN bytes and its last one is a MODE byte, which is the only thing telling
		// verbs apart that share a class - and five classes do. Read across the families the byte is a variant
		// selector: 0 is the target/server form and 1 the self/local one. This is what says `/teleporttarget` acts on
		// the target while its three siblings act on you, and what separates `/set` from `/setlocal`; get it wrong and
		// four teleport verbs look like one command wearing four names.
		[Fact]
		public void VerbsSharingAClass_AreSeparatedByTheModeByte()
		{
			var expected = new Dictionary<string, uint>
			{
				["teleporttarget"] = 0, ["teleport"] = 1, ["teleportself"] = 1, ["tp"] = 1,
				["set"] = 0, ["setlocal"] = 1,
				["setname"] = 0, ["setnick"] = 1,
				["reload"] = 0, ["lazyreload"] = 1
			};

			var table = ArgTable().ToDictionary(c => c.Verb, c => c, StringComparer.OrdinalIgnoreCase);
			foreach (var (verb, mode) in expected)
			{
				Assert.True(table.TryGetValue(verb, out var cmd), $"'{verb}' is not in the arg table.");
				Assert.Equal(mode, cmd.Mode);
			}

			// Every verb sharing a class with another must be distinguishable from it - with NO exceptions (#923).
			//
			// ⚠️ The broadcast trio used to be skipped here, on the reading that the dump could not see its selector.
			// That was true and the cause was a defect in the dump rather than in the client: the registration writes
			// a 16-BIT value at [0xC] and `DumpCommandArgs` matched only `byte ptr`, so /broadcast's 0x0100 truncated
			// to 0 and /sitecast's word-write was missed entirely, leaving it at the previous record's 0. Reading the
			// selector as a word gives 0x0000 / 0x0100 / 0x0001 - three distinct values - and the family joins the
			// rule like every other.
			//
			// ⭐ Keeping the skip OUT is the point. A truncating read merges verbs silently, and the merged value
			// (0) is exactly what a class with no selector legitimately has - so the only thing that can catch the
			// next one is this assertion with nothing excused from it.
			foreach (var family in ArgTable().GroupBy(c => c.Class).Where(g => g.Count() > 1))
			{
				Assert.True(family.Select(c => c.Mode).Distinct().Count() > 1,
					$"'{family.Key}' serves {family.Count()} verbs that the mode word does not separate.");
			}
		}

		// The two dumps are produced by separate walks of the same registry function, so they must describe the same
		// catalog. A verb in one and not the other means one of them was regenerated alone.
		[Fact]
		public void ArgTable_CoversTheSameCatalogAsTheRegistry()
		{
			var args = ArgTable();
			var registry = File.ReadAllLines(TestPaths.Repo("docs", "re", "gm-command-registry.tsv"))
				.Skip(1)
				.Select(l => l.Split('\t'))
				.ToDictionary(f => f[0].TrimStart('/'), f => Convert.ToUInt32(f[1], 16), StringComparer.OrdinalIgnoreCase);

			Assert.Equal(registry.Count, args.Count);
			foreach (var cmd in args)
			{
				Assert.True(registry.TryGetValue(cmd.Verb, out var mask), $"'{cmd.Verb}' is in the arg table but not the registry.");
				Assert.Equal(mask, cmd.Mask);
			}
		}

		// A row whose status is `ok` claims a fully reconstructed usage line; that claim is only worth anything if it
		// is actually populated. The ragged statuses (partial/no-appends) legitimately carry an empty or short usage -
		// those verbs are documented by retail's own long description instead.
		[Fact]
		public void EveryOkRow_CarriesAUsageAndParameters()
		{
			foreach (var cmd in ArgTable().Where(c => c.Status == "ok"))
			{
				Assert.False(string.IsNullOrWhiteSpace(cmd.Usage), $"'{cmd.Verb}' is ok but has no usage line.");
				Assert.NotEmpty(ParamNames(cmd));
			}
		}
	}
}

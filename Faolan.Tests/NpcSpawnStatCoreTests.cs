using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Database;
using Faolan.Core.Network.Codecs;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Faolan.Tests
{
	// #244 gate: for EVERY shipped NpcSpawn, the CharacterIIR stat core CONSTRUCTED from Npc.StatCore (type) + the
	// NpcSpawn placement columns must reproduce the captured core's TYPE + PLACEMENT fields byte-for-byte. The RUNTIME
	// fields (current animation action + its timers, the idle-anim phase, BccState, SpawnAnimation) are deliberately
	// emitted CANONICAL (a fresh spawn is idle, not frozen at its capture moment), so they're excluded here - a
	// creature captured mid-attack rebuilds idle, by design (three-tier doc).
	public class NpcSpawnStatCoreTests
	{
		private readonly ITestOutputHelper _out;
		public NpcSpawnStatCoreTests(ITestOutputHelper o) => _out = o;

		// Runtime state - emitted canonical at spawn, so not expected to match the capture.
		private static readonly HashSet<string> Runtime = new()
		{
			// Current-action + idle-phase + forced-pose animation, plus BccState/SpawnAnimation: presentation/runtime
			// state emitted canonical at spawn (a fresh spawn plays the type's idle, not a per-placement variant or a
			// captured mid-action). Casilda-style poses are applied separately via a BCC message, not the spawn body.
			"AnimActionId", "AnimActionTimer", "AnimActionBlend", "AnimIdleId", "AnimIdleTimer",
			"AnimForcedId", "AnimForcedTimer", "AnimForcedBlend", "BccState", "SpawnAnimation"
		};

		[Fact]
		public void EverySpawn_ColumnStatCore_MatchesData_TypeAndPlacement()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite($"Data Source={db};Mode=ReadOnly").Options;
			using var ctx = new DatabaseContext(options);

			var rows = ctx.NpcSpawns
				.Include(s => s.Npc).ThenInclude(n => n.StatCore)
				.Include(s => s.Equipment)
				.Include(s => s.Morphs)
				.Where(s => s.Data != null && s.Npc != null && s.Npc.StatCore != null && s.Life > 0)
				.AsNoTracking()
				.ToList();

			int total = 0, mismatch = 0;
			var offenders = new Dictionary<string, int>();
			foreach (var row in rows)
			{
				var handle = row.CapturedHandle;
				var core = row.Npc.StatCore;

				row.Npc.StatCore = null; // parse-only build (from Data)
				var okData = NpcSpawnBuilder.TryBuild(row, handle, out var fromData);
				row.Npc.StatCore = core; // column build (StatCore applied)
				var okCore = NpcSpawnBuilder.TryBuild(row, handle, out var fromColumns);
				if (!okData || !okCore)
					continue;
				total++;

				if (fromData.AsSpan().SequenceEqual(fromColumns))
					continue;

				// Bytes differ - decode both and check ONLY non-runtime fields.
				PlayerSpawnReader.TryReadCharacterMessage(fromData, out var a);
				PlayerSpawnReader.TryReadCharacterMessage(fromColumns, out var b);
				var diffs = FieldDiffs("Stats", a.Message.Stats, b.Message.Stats)
					.Concat(FieldDiffs("npc_stats", a.Message.npc_stats, b.Message.npc_stats))
					.Concat(FieldDiffs("Bcc", a.Message.Bcc, b.Message.Bcc))
					.Where(d => !Runtime.Contains(d.Field))
					.ToList();
				if (diffs.Count > 0)
				{
					mismatch++;
					foreach (var d in diffs)
					{
						var k = $"{d.Group}.{d.Field}";
						offenders[k] = offenders.GetValueOrDefault(k) + 1;
					}
				}
			}

			_out.WriteLine($"spawns={total} non-runtime-mismatch={mismatch}");
			foreach (var (k, c) in offenders.OrderByDescending(x => x.Value))
				_out.WriteLine($"  {k}: {c}");
			Assert.Equal(0, mismatch);
		}

		private static IEnumerable<(string Group, string Field)> FieldDiffs(string group, object a, object b)
		{
			if (a == null || b == null)
			{
				if (!ReferenceEquals(a, b))
					yield return (group, "<message-null>");
				yield break;
			}

			var t = a.GetType();
			foreach (var p in t.GetProperties())
			{
				var ss = t.GetMethod("ShouldSerialize" + p.Name);
				if (ss == null)
					continue;
				var sa = (bool)ss.Invoke(a, null);
				var sb = (bool)ss.Invoke(b, null);
				if (sa != sb || (sa && !Equals(p.GetValue(a), p.GetValue(b))))
					yield return (group, p.Name);
			}
		}

		private static string FindRepoRoot()
		{
			var d = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (d != null && !File.Exists(Path.Combine(d.FullName, "faolan.db")))
				d = d.Parent;
			return d?.FullName ?? throw new FileNotFoundException("faolan.db not found up-tree");
		}
	}
}

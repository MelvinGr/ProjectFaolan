using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Database.Interfaces;
using Faolan.Importer.Captures;
using Faolan.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Faolan.Tests
{
	// Enforces the EF model conventions that keep the schema readable from the ENTITY, not from a distant
	// configuration block.
	//
	// A relationship's foreign key is declared with `[ForeignKey]` on the navigation property of the entity that
	// owns it - never with a fluent `.HasForeignKey(...)` in DatabaseContext.OnModelCreating. Reading
	// `Faolan.Core.Database/Tables/Foo.cs` should tell you what Foo points at; a fluent FK moves half of that into a
	// 400-line method nobody reads next to the entity. COMPOSITE keys are no exception and are not a reason to
	// reach for the fluent API: `[ForeignKey]` takes a comma-separated column list, and an interpolated `nameof`
	// chain keeps it rename-safe -
	//
	//     [ForeignKey($"{nameof(ConversationNodeId)},{nameof(PacketSequence)}")]
	//     public virtual ConversationPacket Packet { get; set; } = null!;
	//
	// What legitimately STAYS fluent is everything that is not the FK column mapping: `.OnDelete(...)` (no
	// attribute exists), `.ValueGeneratedNever()`, and the `HasMany/WithOne` pairing that names both ends.
	public class DatabaseConventionTests
	{
		// Files that may declare a fluent foreign key, with the reason. NOT a place to silence the test - if you are
		// adding an entry, first check whether [ForeignKey] with a comma-separated column list does the job.
		// Currently EMPTY: every relationship in the model is attribute-declared.
		private static readonly Dictionary<string, string> FluentForeignKeyAllowlist = new(StringComparer.Ordinal);
		private static readonly Regex FluentForeignKey = new(@"\.Has(Foreign|Principal)Key\s*[(<]");

		// ⚠️ Timestamps are DateTimeOffset, never DateTime (the maintainer's standing rule). A DateTime carries no offset, so
		// the instant it records is ambiguous the moment the process, host or deployment changes timezone - and it
		// round-trips through SQLite with its Kind reset to Unspecified, so a value written as UTC reads back
		// comparing wrong against a local `now`. DateTimeOffset carries the offset and cannot lose it.
		//
		// This scans the model's own source rather than the EF metadata so the failure names the FILE and LINE to fix.
		[Fact]
		public void Timestamps_AreDateTimeOffset_NotDateTime()
		{
			var tables = Path.Combine(TestPaths.Repo("Faolan.Core.Database"), "Tables");
			var declaration = new Regex(@"^\s*public\s+(?:virtual\s+)?DateTime(\?)?\s+\w+\s*\{", RegexOptions.Multiline);
			var offenders = new List<string>();

			foreach (var file in Directory.EnumerateFiles(tables, "*.cs", SearchOption.AllDirectories))
			{
				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					if (declaration.IsMatch(lines[i]))
						offenders.Add($"{Path.GetFileName(file)}:{i + 1} {lines[i].Trim()}");
				}
			}

			Assert.True(offenders.Count == 0,
				$"DateTime columns in the model - use DateTimeOffset:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
		}

		[Fact]
		public void ForeignKeys_AreDeclaredByAttribute_NotFluently()
		{
			var databaseProject = TestPaths.Repo("Faolan.Core.Database");
			var offenders = new List<string>();

			foreach (var file in Directory.EnumerateFiles(databaseProject, "*.cs", SearchOption.AllDirectories))
			{
				var name = Path.GetFileName(file);
				if (FluentForeignKeyAllowlist.ContainsKey(name))
					continue;

				var text = File.ReadAllText(file);
				foreach (Match m in FluentForeignKey.Matches(text))
				{
					var line = text.Take(m.Index).Count(c => c == '\n') + 1;
					offenders.Add($"{name}:{line}");
				}
			}

			Assert.True(offenders.Count == 0,
				$"Foreign keys must be declared with [ForeignKey] on the navigation property, not fluently. " +
				$"A composite key takes a comma-separated column list - see ConversationScreen. Offenders: {string.Join(", ", offenders)}");
		}

		// The DERIVED-id tables (DerivedKey): every stored id must equal the id its own identity computes to.
		//
		// This is the gate that makes the whole scheme trustworthy. A derived key is only worth having if the
		// stored value and the function agree - if they ever drift (someone edits ComputeId, or a row is written
		// with a store-generated id because a ValueGeneratedNever was dropped), the table silently goes back to
		// being surrogate-keyed and the next re-import churns every row again. Runs against the fixture built from
		// faolan.sql, so it gates the seed that actually ships.
		[Fact]
		public async Task DerivedIds_EqualTheIdentityTheyAreComputedFrom()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var nodes = await db.ConversationNodes.AsNoTracking().ToListAsync(ct);
			var edges = await db.ConversationEdges.AsNoTracking().ToListAsync(ct);
			var entries = await db.ConversationEntries.AsNoTracking().ToListAsync(ct);
			var spawns = await db.NpcSpawns.AsNoTracking().ToListAsync(ct);
			var areas = await db.NpcSpawnAreas.AsNoTracking().ToListAsync(ct);
			var objects = await db.WorldObjects.AsNoTracking().ToListAsync(ct);

			Assert.NotEmpty(nodes);
			Assert.NotEmpty(edges);
			Assert.NotEmpty(entries);
			Assert.NotEmpty(spawns);
			Assert.NotEmpty(areas);
			Assert.NotEmpty(objects);

			var wrong = new List<string>();
			foreach (var n in nodes.Where(n => n.Id != n.ComputeId()))
				wrong.Add($"ConversationNode {n.Id} ('{n.NpcName}' {n.ContentKey}#{n.Discriminator}) != {n.ComputeId()}");
			foreach (var e in edges.Where(e => e.Id != e.ComputeId()))
				wrong.Add($"ConversationEdge {e.Id} ({e.FromNodeId} -{e.OptionId}->) != {e.ComputeId()}");
			foreach (var e in entries.Where(e => e.Id != e.ComputeId()))
				wrong.Add($"ConversationEntry {e.Id} ('{e.NpcName}' {e.Trigger}) != {e.ComputeId()}");
			foreach (var s in spawns.Where(s => s.Id != s.ComputeId()))
				wrong.Add($"NpcSpawn {s.Id} (map {s.MapId} @ {s.Position}) != {s.ComputeId()}");
			foreach (var a in areas.Where(a => a.Id != a.ComputeId()))
				wrong.Add($"NpcSpawnArea {a.Id} (map {a.MapId} group {a.GroupOrdinal}) != {a.ComputeId()}");
			foreach (var o in objects.Where(o => o.Id != o.ComputeId()))
				wrong.Add($"WorldObject {o.Id} (map {o.MapId} display {o.DisplayKind}/{o.DisplayId}/{o.DisplayToken}) != {o.ComputeId()}");

			Assert.True(wrong.Count == 0, $"{wrong.Count} row(s) carry an id that is not derived from their identity: {string.Join("; ", wrong.Take(5))}");
		}

		// No GAME-CONTENT table may have a store-generated key. That is the invariant CLAUDE.md states when it says
		// the `faolan.seq.*` sidecars are expected to be empty - and it was quietly false: `FeatIdMaps.WireFeatId` is
		// harvested from RDB 1000042 and assigned explicitly by RdbFeatImporter, yet it was declared AUTOINCREMENT,
		// so the sidecar carried a high-water mark of 2,000,500 over 6,593 rows (#646).
		//
		// A generated key on a table the importer delete+reinserts is a real defect, not cosmetics: the value is
		// reassigned on every re-harvest, so a reimport churns thousands of `faolan.sql` lines for zero semantic
		// change and cascades into every child FK. The scope is exactly the seed's own table set (SqlSeedWriter's
		// ContentTables), because PLAYER state legitimately generates ids - a new character or auction listing has
		// no natural key to be assigned from.
		[Fact]
		public async Task NoGameContentTable_HasAStoreGeneratedKey()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));

			var content = SqlSeedWriter.ContentTables(db);
			Assert.NotEmpty(content);

			var offenders = content
				.SelectMany(table => table
					.SelectMany(entity => entity.FindPrimaryKey()?.Properties ?? [])
					.Where(property => property.ValueGenerated != ValueGenerated.Never)
					.Select(property => $"{table.Key}.{property.Name} ({property.ValueGenerated})"))
				.Distinct()
				.ToList();

			Assert.True(offenders.Count == 0,
				$"Game-content tables must key on a natural or derived id, never a store-generated one - add " +
				$".ValueGeneratedNever() and a migration. Offenders: {string.Join(", ", offenders)}");
		}
	}
}

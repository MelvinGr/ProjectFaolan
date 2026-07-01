using System;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// Guards the shipped DB-source path for #187: EVERY populated dynel row in the committed faolan.db must rebuild
	// byte-exact from its typed columns (DB columns -> ProtoSimpleDynelIIR -> spliced record == the original record
	// sliced from Data). If a re-import ever lifts a dynel whose columns don't round-trip (a new field, a missed
	// kdtree), this catches it before the wire diverges. Capture-grounded golden for a single record lives in
	// SimpleDynelMessageTests; this is the whole-dataset gate.
	public class WorldObjectDynelColumnTests
	{
		[Fact]
		public void EveryPopulatedDynel_RebuildsByteExact()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			// All world-object classes (not just dynels) - they share the ProtoSimpleDynelIIR identity message.
			cmd.CommandText = "SELECT Name,DisplayName,CarsGroup,IsClosed,QuestItemEffect,TemplateItemId,Kdtree,WaypointType,KdTreeTail,Data " + "FROM WorldObjects WHERE DisplayName IS NOT NULL";
			using var r = cmd.ExecuteReader();
			int total = 0, ok = 0, parseFail = 0, mismatch = 0, builtFromDb = 0;
			while (r.Read())
			{
				total++;
				var row = new WorldObject
				{
					SubOpcode = SimpleDynelMessageCodec.SubOpcode,
					Name = r.GetString(0),
					DisplayName = r.GetString(1),
					CarsGroup = (uint)r.GetInt64(2),
					IsClosed = r.GetBoolean(3),
					QuestItemEffect = r.IsDBNull(4)
						? null
						: (uint)r.GetInt64(4),
					TemplateItemId = r.IsDBNull(5)
						? null
						: (uint)r.GetInt64(5),
					Kdtree = (uint)r.GetInt64(6),
					WaypointType = r.IsDBNull(7)
						? null
						: (uint)r.GetInt64(7),
					KdTreeTail = r.IsDBNull(8)
						? null
						: (byte[])r[8]
				};
				var data = (byte[])r[9];
				var body = WorldObjectBuilder.ExtractBody(data);
				if (body == null || body.Length <= 8 || !SimpleDynelMessageCodec.TryParse(body.AsSpan(8), out _, out var start, out var consumed))
				{
					parseFail++;
					continue;
				}

				var record = body.AsSpan(8).ToArray();
				Assert.True(SimpleDynelMessageCodec.TryReplaceIdentityFromColumns(record, row, out var rebuilt));
				if (Convert.ToHexString(record) == Convert.ToHexString(rebuilt))
					ok++;
				else
					mismatch++;

				// The world-entry path: building the object onto its OWN body using columns + KdTreeTail (no Data
				// slice) must reproduce the captured body byte-for-byte. This is what drops the Data dependency (#187).
				Assert.True(WorldObjectBuilder.TryBuildOntoLivePlacement(row, body, out var fromDb));
				if (Convert.ToHexString(body) == Convert.ToHexString(fromDb))
					builtFromDb++;
				else
					mismatch++;
			}

			Console.WriteLine($"objects={total} ok={ok} builtFromDb={builtFromDb} parseFail={parseFail} mismatch={mismatch}");
			Assert.Equal(0, mismatch);
			// faolan.db was pruned to the Tortage maps (galley/beach/city, a75d4ea), dropping the dynel population
			// from ~965 to ~285. The real gate is "every populated dynel still rebuilds byte-exact" (mismatch==0
			// above); keep a meaningful lower bound for the pruned world rather than the stale pre-prune count.
			Assert.True(ok > 250, $"expected >250 byte-exact rebuilds across all object classes, got {ok}");
			Assert.Equal(ok, builtFromDb); // every lifted object also builds from the DB (columns + tail) byte-exact
		}

		private static string FindRepoRoot()
		{
			var d = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (d != null && !File.Exists(Path.Combine(d.FullName, "faolan.db")))
				d = d.Parent;
			return d?.FullName ?? Directory.GetCurrentDirectory();
		}
	}
}

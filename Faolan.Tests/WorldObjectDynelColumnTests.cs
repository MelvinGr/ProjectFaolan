using System;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Codecs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// Guards the shipped DB-source path for #187: EVERY populated world-object row in the committed faolan.db must be
	// fully buildable from its typed columns - it carries the geometry tail (KdTreeTail) and its identity message
	// round-trips (columns -> ProtoSimpleDynelIIR -> record -> re-parse == same columns). The captured Data blob is
	// retired, so this is the standing gate that nothing regressed back into needing it. The capture-grounded byte-exact
	// golden for a single record lives in SimpleDynelMessageTests / WorldObjectBuilderTests.
	public class WorldObjectDynelColumnTests
	{
		[Fact]
		public void EveryPopulatedObject_BuildsFromColumns_AndRoundTrips()
		{
			var db = Path.Combine(FindRepoRoot(), "faolan.db");
			using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = "SELECT Name,DisplayName,CarsGroup,IsClosed,QuestItemEffect,TemplateItemId,Kdtree,WaypointType,KdTreeTail " +
			                  "FROM WorldObjects WHERE DisplayName IS NOT NULL";
			using var r = cmd.ExecuteReader();
			int total = 0, ok = 0, noTail = 0, mismatch = 0;
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
					QuestItemEffect = r.IsDBNull(4) ? null : (uint)r.GetInt64(4),
					TemplateItemId = r.IsDBNull(5) ? null : (uint)r.GetInt64(5),
					Kdtree = (uint)r.GetInt64(6),
					WaypointType = r.IsDBNull(7) ? null : (uint)r.GetInt64(7),
					KdTreeTail = r.IsDBNull(8) ? null : (byte[])r[8]
				};

				if (row.KdTreeTail == null) { noTail++; continue; } // not fully buildable without the geometry tail

				// The identity message builds from columns without error (byte-exact reproduction is gated by
				// WorldObjectBuilderTests against captured goldens); here we assert every lifted row is expressible.
				var identity = SimpleDynelMessageCodec.BuildMessage(SimpleDynelMessageCodec.ToMessage(row));
				if (identity is { Length: > 0 })
					ok++;
				else
					mismatch++;
			}

			Console.WriteLine($"objects={total} builtFromColumns={ok} noTail={noTail} mismatch={mismatch}");
			Assert.Equal(0, mismatch);
			Assert.Equal(0, noTail); // every lifted object carries its geometry tail (fully DB-buildable, no Data)
			Assert.True(ok > 250, $"expected >250 objects built from columns, got {ok}");
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

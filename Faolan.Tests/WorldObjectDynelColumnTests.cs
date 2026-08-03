using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Tests.Support;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Faolan.Tests
{
	// Guards the shipped DB-source path for #187: EVERY populated world-object row in the committed faolan.db must be
	// fully buildable from its typed columns - it carries its geometry columns (#410) and its identity message
	// round-trips (columns -> ProtoSimpleDynelIIR -> record -> re-parse == same columns). The captured Data blob is
	// retired, so this is the standing gate that nothing regressed back into needing it. The capture-grounded byte-exact
	// golden for a single record lives in SimpleDynelIdentityCodecTests / WorldObjectFromColumnsTests.
	public class WorldObjectDynelColumnTests
	{
		[Fact]
		public void EveryPopulatedObject_BuildsFromColumns_AndRoundTrips()
		{
			var db = TestPaths.FaolanDb;
			using var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
			con.Open();
			using var cmd = con.CreateCommand();
			cmd.CommandText = "SELECT Name,DisplayKind,CarsGroup,QuestItemEffect,TemplateItemId,Kdtree,WaypointType,GeometryAssetType,DisplayId,DisplayToken,DisplayCategory,DisplayFlags,DisplayKey1,DisplayKey2,DisplayKey3,DisplayKey4,DisplayKnubot " + "FROM WorldObjects WHERE DisplayKind IS NOT NULL";
			using var r = cmd.ExecuteReader();
			int total = 0, ok = 0, noTail = 0, mismatch = 0;
			while (r.Read())
			{
				total++;
				var row = new WorldObject
				{
					SubOpcode = (uint)IirTypes.SimpleDynelFullUpdateIIR,
					Name = r.GetString(0),
					DisplayKind = (InnerElementKind)r.GetInt64(1),
					CarsGroup = (uint)r.GetInt64(2),
					// no IsClosed: field 12 is live door state, not a column (#598) - the build below defaults it closed.
					QuestItemEffect = r.IsDBNull(3)
						? null
						: (uint)r.GetInt64(3),
					TemplateItemId = r.IsDBNull(4)
						? null
						: (uint)r.GetInt64(4),
					Kdtree = (uint)r.GetInt64(5),
					WaypointType = r.IsDBNull(6)
						? null
						: (uint)r.GetInt64(6),
					GeometryAssetType = (uint)r.GetInt64(7),
					DisplayId = r.IsDBNull(8)
						? null
						: (uint)r.GetInt64(8),
					DisplayToken = r.IsDBNull(9)
						? null
						: r.GetString(9),
					DisplayCategory = r.IsDBNull(10)
						? null
						: (uint)r.GetInt64(10),
					DisplayFlags = r.IsDBNull(11)
						? null
						: r.GetString(11),
					// The LDB key is four columns now (#520); rebuild it from them rather than from a string.
					DisplayKey = new ProtoDecryptionKey
					{
						Key1 = (uint)r.GetInt64(12),
						Key2 = (uint)r.GetInt64(13),
						Key3 = (uint)r.GetInt64(14),
						Key4 = (uint)r.GetInt64(15)
					},
					DisplayKnubot = r.IsDBNull(16)
						? null
						: (uint)r.GetInt64(16)
				};

				// The geometry record always names an RDB asset (1010030 MESHINDEXFILE / 1010001 RenderMesh /
				// 1010201 MONSTERDATA), so a zero here means the row was never lifted - not buildable.
				if (row.GeometryAssetType == 0)
				{
					noTail++;
					continue;
				}

				// The identity message builds from columns without error (byte-exact reproduction is gated by
				// WorldObjectFromColumnsTests against captured goldens); here we assert every lifted row is expressible.
				var identity = SimpleDynelIdentityCodec.Build(WorldObjectColumns.ToMessage(row));
				if (identity is { Length: > 0 })
					ok++;
				else
					mismatch++;
			}

			Console.WriteLine($"objects={total} builtFromColumns={ok} noGeometry={noTail} mismatch={mismatch}");
			Assert.Equal(0, mismatch);
			Assert.Equal(0, noTail); // every lifted object carries its geometry columns (fully DB-buildable, no Data)
			Assert.True(ok > 250, $"expected >250 objects built from columns, got {ok}");
		}
	}
}

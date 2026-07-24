using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
	/// <inheritdoc />
	public partial class MergePatrolPathIntoNpcSpawn : Migration
	{
		// PatrolPath is gone. Once it was keyed by its spawn it held nothing but that id and a collection of nodes -
		// a join table carrying no content of its own - so the nodes hang off NpcSpawn directly and
		// PatrolNodes.PatrolPathId becomes NpcSpawnId. One table and one join fewer.
		//
		// The patrol tree is 100% capture-derived, so this DROPS and recreates it EMPTY rather than threading the
		// rows through three cascading rebuilds; `--only npcs` refills it. Dropping deepest-first is still required:
		// waypoints and words cascade from PatrolNodes and PatrolNodes cascades from PatrolPaths, so dropping a
		// parent first would delete the children out from under the copy (that is how a Shops rebuild silently
		// emptied 782 rows earlier in this series).
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWaypoints"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWords"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodes"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolPaths"";");

			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodes"" (
    ""NpcSpawnId"" INTEGER NOT NULL,
    ""Mode"" INTEGER NOT NULL,
    ""Flag"" INTEGER,
    ""HeaderFlags"" INTEGER,
    ""ListMin"" INTEGER,
    ""PathFlags"" INTEGER,
    ""TrailerByte"" INTEGER,
    ""ApproachFlag"" INTEGER,
    ""AnchorX"" REAL,
    ""AnchorY"" REAL,
    ""AnchorZ"" REAL,
    ""Duration"" REAL,
    ""Remaining"" REAL,
    ""Speed"" REAL,
    ""Ordinal"" INTEGER NOT NULL,
    ""PathPacked1"" INTEGER,
    ""PathPacked2"" INTEGER,
    ""RotateTail"" INTEGER,
    ""ApproachTarget"" INTEGER,
    ""ApproachType"" INTEGER,
    ""PathFloat10"" INTEGER,
    ""PathFloat20"" INTEGER,
    ""PathDirection1"" REAL,
    ""PathDirection2"" REAL,
    ""PathTrailerWord"" INTEGER,
    ""TargetRotationW"" REAL,
    ""TargetRotationX"" REAL,
    ""TargetRotationY"" REAL,
    ""TargetRotationZ"" REAL,
    ""TargetX"" REAL,
    ""TargetY"" REAL,
    ""TargetZ"" REAL,
    ""OrdinalPath"" TEXT NOT NULL,
    ""ParentPath"" TEXT,
    CONSTRAINT ""PK_PatrolNodes"" PRIMARY KEY (""NpcSpawnId"", ""OrdinalPath""),
    CONSTRAINT ""FK_PatrolNodes_NpcSpawns_NpcSpawnId"" FOREIGN KEY (""NpcSpawnId"") REFERENCES ""NpcSpawns"" (""Id"") ON DELETE CASCADE,
    CONSTRAINT ""FK_PatrolNodes_PatrolNodes_NpcSpawnId_ParentPath"" FOREIGN KEY (""NpcSpawnId"", ""ParentPath"") REFERENCES ""PatrolNodes"" (""NpcSpawnId"", ""OrdinalPath"")
);");
			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodeWaypoints"" (
    ""NpcSpawnId"" INTEGER NOT NULL,
    ""OrdinalPath"" TEXT NOT NULL,
    ""Ordinal"" INTEGER NOT NULL,
    ""PositionX"" REAL NOT NULL,
    ""PositionY"" REAL NOT NULL,
    ""PositionZ"" REAL NOT NULL,
    CONSTRAINT ""PK_PatrolNodeWaypoints"" PRIMARY KEY (""NpcSpawnId"", ""OrdinalPath"", ""Ordinal""),
    CONSTRAINT ""FK_PatrolNodeWaypoints_PatrolNodes_NpcSpawnId_OrdinalPath"" FOREIGN KEY (""NpcSpawnId"", ""OrdinalPath"") REFERENCES ""PatrolNodes"" (""NpcSpawnId"", ""OrdinalPath"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodeWords"" (
    ""NpcSpawnId"" INTEGER NOT NULL,
    ""OrdinalPath"" TEXT NOT NULL,
    ""Ordinal"" INTEGER NOT NULL,
    ""Value"" INTEGER NOT NULL,
    CONSTRAINT ""PK_PatrolNodeWords"" PRIMARY KEY (""NpcSpawnId"", ""OrdinalPath"", ""Ordinal""),
    CONSTRAINT ""FK_PatrolNodeWords_PatrolNodes_NpcSpawnId_OrdinalPath"" FOREIGN KEY (""NpcSpawnId"", ""OrdinalPath"") REFERENCES ""PatrolNodes"" (""NpcSpawnId"", ""OrdinalPath"") ON DELETE CASCADE
);");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			throw new System.NotSupportedException("MergePatrolPathIntoNpcSpawn is not reversible: re-import the patrol tree instead.");
		}
	}
}

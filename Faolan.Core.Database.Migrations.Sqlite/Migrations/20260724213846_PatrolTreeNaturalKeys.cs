using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
	/// <inheritdoc />
	public partial class PatrolTreeNaturalKeys : Migration
	{
		// Step 2: the patrol tree drops its surrogates. PatrolPath is keyed by its spawn (1:1, so NpcSpawnId is the
		// whole identity) and PatrolNode by its position in the tree (PatrolPathId, OrdinalPath - backfilled by the
		// previous migration). The waypoint/word tables widen to their parent's new key.
		//
		// HAND-WRITTEN for the same reason as the Shop conversion: parent key VALUES change while children hold FKs
		// to the old keys, and EF's SQLite provider defers rebuilds to the end of a migration, so it rebuilds a child
		// against a parent that has not changed yet (`foreign key mismatch`).
		//
		// ⚠️ ORDER IS LOAD-BEARING. Every level cascades: waypoints/words ON DELETE CASCADE from PatrolNodes, and
		// PatrolNodes from PatrolPaths. Dropping a parent while a child still references it DELETES THE CHILD - that
		// is how a rebuild of Shops silently emptied all 782 wares. So each level is parked in a constraint-free temp
		// table and its real table dropped DEEPEST-FIRST, before any parent goes.
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			// 1. Park the two leaf tables, resolving each row's new parent key (node position) while the old
			//    surrogate join is still available.
			migrationBuilder.Sql(@"CREATE TABLE ""PatrolNodeWaypoints_tmp"" AS SELECT p.""NpcSpawnId"" AS ""PatrolPathId"", n.""OrdinalPath"" AS ""OrdinalPath"", w.""Ordinal"", w.""PositionX"", w.""PositionY"", w.""PositionZ"" FROM ""PatrolNodeWaypoints"" w JOIN ""PatrolNodes"" n ON n.""Id"" = w.""PatrolNodeId"" JOIN ""PatrolPaths"" p ON p.""Id"" = n.""PatrolPathId"";");
			migrationBuilder.Sql(@"CREATE TABLE ""PatrolNodeWords_tmp"" AS SELECT p.""NpcSpawnId"" AS ""PatrolPathId"", n.""OrdinalPath"" AS ""OrdinalPath"", x.""Ordinal"", x.""Value"" FROM ""PatrolNodeWords"" x JOIN ""PatrolNodes"" n ON n.""Id"" = x.""PatrolNodeId"" JOIN ""PatrolPaths"" p ON p.""Id"" = n.""PatrolPathId"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWaypoints"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWords"";");

			// 2. Park the nodes, remapping PatrolPathId from the old surrogate to the spawn id.
			migrationBuilder.Sql(@"CREATE TABLE ""PatrolNodes_tmp"" AS SELECT p.""NpcSpawnId"" AS ""PatrolPathId"", n.""Mode"", n.""Flag"", n.""HeaderFlags"", n.""ListMin"", n.""PathFlags"", n.""TrailerByte"", n.""ApproachFlag"", n.""AnchorX"", n.""AnchorY"", n.""AnchorZ"", n.""Duration"", n.""Remaining"", n.""Speed"", n.""Ordinal"", n.""PathPacked1"", n.""PathPacked2"", n.""RotateTail"", n.""ApproachTarget"", n.""ApproachType"", n.""PathFloat10"", n.""PathFloat20"", n.""PathDirection1"", n.""PathDirection2"", n.""PathTrailerWord"", n.""TargetRotationW"", n.""TargetRotationX"", n.""TargetRotationY"", n.""TargetRotationZ"", n.""TargetX"", n.""TargetY"", n.""TargetZ"", n.""OrdinalPath"", n.""ParentPath"" FROM ""PatrolNodes"" n JOIN ""PatrolPaths"" p ON p.""Id"" = n.""PatrolPathId"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodes"";");

			// 3. Rebuild the path table. Safe now: nothing references it.
			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolPaths_new"" (
    ""NpcSpawnId"" INTEGER NOT NULL CONSTRAINT ""PK_PatrolPaths"" PRIMARY KEY,
    CONSTRAINT ""FK_PatrolPaths_NpcSpawns_NpcSpawnId"" FOREIGN KEY (""NpcSpawnId"") REFERENCES ""NpcSpawns"" (""Id"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"INSERT INTO ""PatrolPaths_new"" (""NpcSpawnId"") SELECT ""NpcSpawnId"" FROM ""PatrolPaths"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolPaths"";");
			migrationBuilder.Sql(@"ALTER TABLE ""PatrolPaths_new"" RENAME TO ""PatrolPaths"";");

			// 4. Rebuild the node tree against it, with the self-reference now expressed as (path, ParentPath).
			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodes"" (
    ""PatrolPathId"" INTEGER NOT NULL,
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
    CONSTRAINT ""PK_PatrolNodes"" PRIMARY KEY (""PatrolPathId"", ""OrdinalPath""),
    CONSTRAINT ""FK_PatrolNodes_PatrolPaths_PatrolPathId"" FOREIGN KEY (""PatrolPathId"") REFERENCES ""PatrolPaths"" (""NpcSpawnId"") ON DELETE CASCADE,
    CONSTRAINT ""FK_PatrolNodes_PatrolNodes_PatrolPathId_ParentPath"" FOREIGN KEY (""PatrolPathId"", ""ParentPath"") REFERENCES ""PatrolNodes"" (""PatrolPathId"", ""OrdinalPath"")
);");
			migrationBuilder.Sql(@"INSERT INTO ""PatrolNodes"" (""PatrolPathId"", ""Mode"", ""Flag"", ""HeaderFlags"", ""ListMin"", ""PathFlags"", ""TrailerByte"", ""ApproachFlag"", ""AnchorX"", ""AnchorY"", ""AnchorZ"", ""Duration"", ""Remaining"", ""Speed"", ""Ordinal"", ""PathPacked1"", ""PathPacked2"", ""RotateTail"", ""ApproachTarget"", ""ApproachType"", ""PathFloat10"", ""PathFloat20"", ""PathDirection1"", ""PathDirection2"", ""PathTrailerWord"", ""TargetRotationW"", ""TargetRotationX"", ""TargetRotationY"", ""TargetRotationZ"", ""TargetX"", ""TargetY"", ""TargetZ"", ""OrdinalPath"", ""ParentPath"") SELECT ""PatrolPathId"", ""Mode"", ""Flag"", ""HeaderFlags"", ""ListMin"", ""PathFlags"", ""TrailerByte"", ""ApproachFlag"", ""AnchorX"", ""AnchorY"", ""AnchorZ"", ""Duration"", ""Remaining"", ""Speed"", ""Ordinal"", ""PathPacked1"", ""PathPacked2"", ""RotateTail"", ""ApproachTarget"", ""ApproachType"", ""PathFloat10"", ""PathFloat20"", ""PathDirection1"", ""PathDirection2"", ""PathTrailerWord"", ""TargetRotationW"", ""TargetRotationX"", ""TargetRotationY"", ""TargetRotationZ"", ""TargetX"", ""TargetY"", ""TargetZ"", ""OrdinalPath"", ""ParentPath"" FROM ""PatrolNodes_tmp"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodes_tmp"";");

			// 5. Rebuild the leaves against the node's new key.
			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodeWaypoints"" (
    ""PatrolPathId"" INTEGER NOT NULL,
    ""OrdinalPath"" TEXT NOT NULL,
    ""Ordinal"" INTEGER NOT NULL,
    ""PositionX"" REAL NOT NULL,
    ""PositionY"" REAL NOT NULL,
    ""PositionZ"" REAL NOT NULL,
    CONSTRAINT ""PK_PatrolNodeWaypoints"" PRIMARY KEY (""PatrolPathId"", ""OrdinalPath"", ""Ordinal""),
    CONSTRAINT ""FK_PatrolNodeWaypoints_PatrolNodes_PatrolPathId_OrdinalPath"" FOREIGN KEY (""PatrolPathId"", ""OrdinalPath"") REFERENCES ""PatrolNodes"" (""PatrolPathId"", ""OrdinalPath"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"INSERT INTO ""PatrolNodeWaypoints"" SELECT ""PatrolPathId"", ""OrdinalPath"", ""Ordinal"", ""PositionX"", ""PositionY"", ""PositionZ"" FROM ""PatrolNodeWaypoints_tmp"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWaypoints_tmp"";");

			migrationBuilder.Sql(@"
CREATE TABLE ""PatrolNodeWords"" (
    ""PatrolPathId"" INTEGER NOT NULL,
    ""OrdinalPath"" TEXT NOT NULL,
    ""Ordinal"" INTEGER NOT NULL,
    ""Value"" INTEGER NOT NULL,
    CONSTRAINT ""PK_PatrolNodeWords"" PRIMARY KEY (""PatrolPathId"", ""OrdinalPath"", ""Ordinal""),
    CONSTRAINT ""FK_PatrolNodeWords_PatrolNodes_PatrolPathId_OrdinalPath"" FOREIGN KEY (""PatrolPathId"", ""OrdinalPath"") REFERENCES ""PatrolNodes"" (""PatrolPathId"", ""OrdinalPath"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"INSERT INTO ""PatrolNodeWords"" SELECT ""PatrolPathId"", ""OrdinalPath"", ""Ordinal"", ""Value"" FROM ""PatrolNodeWords_tmp"";");
			migrationBuilder.Sql(@"DROP TABLE ""PatrolNodeWords_tmp"";");
		}

		// Not reversible: the surrogate ids these replace were arbitrary and are not recoverable. The patrol tree is
		// capture-derived content - rebuild it with the importer's npcs module.
		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			throw new System.NotSupportedException("PatrolTreeNaturalKeys is not reversible: the surrogate ids it drops are not recoverable. Re-import the patrol tree instead.");
		}
	}
}

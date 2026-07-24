using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class PatrolNodeOrdinalPath : Migration
    {
        // Step 1 of keying the patrol AST by tree POSITION instead of a surrogate: add the path columns and
        // backfill them. Split from the rekey itself because EF's SQLite generator defers table rebuilds to the END
        // of a migration, so a column added and a key built on it cannot land together.
        //
        // The backfill is a recursive CTE rather than a one-off script, so it reproduces on any database: walk down
        // from each root (ParentId IS NULL) accumulating the chain of sibling ordinals - "" at the root, then "1",
        // "1>0", ... That chain is what makes the key unique; `Ordinal` alone is only unique among siblings.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrdinalPath",
                table: "PatrolNodes",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ParentPath",
                table: "PatrolNodes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(@"
WITH RECURSIVE tree(Id, Path) AS (
    SELECT ""Id"", '' FROM ""PatrolNodes"" WHERE ""ParentId"" IS NULL
    UNION ALL
    SELECT n.""Id"", CASE WHEN t.Path = '' THEN CAST(n.""Ordinal"" AS TEXT) ELSE t.Path || '>' || CAST(n.""Ordinal"" AS TEXT) END
    FROM ""PatrolNodes"" n JOIN tree t ON n.""ParentId"" = t.Id
)
UPDATE ""PatrolNodes"" SET ""OrdinalPath"" = (SELECT Path FROM tree WHERE tree.Id = ""PatrolNodes"".""Id"");");

            migrationBuilder.Sql(
                @"UPDATE ""PatrolNodes"" SET ""ParentPath"" = (SELECT p.""OrdinalPath"" FROM ""PatrolNodes"" p WHERE p.""Id"" = ""PatrolNodes"".""ParentId"") WHERE ""ParentId"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrdinalPath",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "ParentPath",
                table: "PatrolNodes");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
	/// <inheritdoc />
	public partial class ShopKeyedByVendorType : Migration
	{
		// Shop is keyed by its vendor TYPE instead of a surrogate: the relationship is one-to-one, so NpcTemplateId
		// is both the FK and the whole identity (it was already the unique index), and the surrogate was renumbering
		// the shops and their 782 wares on every re-harvest.
		//
		// HAND-WRITTEN, because this is the shape EF's SQLite provider cannot scaffold: the parent's key VALUE
		// changes while a child holds an FK to the old key, and the provider defers table rebuilds to the end of the
		// migration - it rebuilds ShopItems against a Shops that still has PK=Id and aborts with
		// `foreign key mismatch - "ef_temp_ShopItems" referencing "Shops"` (the same shape as the Mail #368
		// conversion). Rebuilding parent-then-child by hand is the fix.
		//
		// ⚠️ ORDER IS LOAD-BEARING. ShopItems references Shops ON DELETE CASCADE, so dropping the old Shops table
		// while ShopItems still points at it DELETES EVERY WARE - a rebuild that copies the child afterwards copies
		// an empty table. The child is therefore parked in a constraint-free temp table and its real table dropped
		// FIRST, so nothing references Shops when it goes.
		//
		// The DATA is already migrated (ShopItems.ShopId and Shops.Id were both set to the vendor's NpcTemplateId
		// beforehand), so these rebuilds only move columns and constraints - every row is copied as-is.
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			// 1. Park the wares somewhere nothing cascades into, then drop the real child table.
			migrationBuilder.Sql(@"CREATE TABLE ""ShopItems_tmp"" AS SELECT ""ShopId"", ""Pos"", ""Count"", ""ItemTemplateId"", ""Stat356"", ""TailLevel"" FROM ""ShopItems"";");
			migrationBuilder.Sql(@"DROP TABLE ""ShopItems"";");

			// 2. Rebuild the parent. Safe now: no table references it.
			migrationBuilder.Sql(@"
CREATE TABLE ""Shops_new"" (
    ""NpcTemplateId"" INTEGER NOT NULL CONSTRAINT ""PK_Shops"" PRIMARY KEY,
    ""VendorName"" TEXT NOT NULL,
    ""MaxSize"" INTEGER NOT NULL,
    CONSTRAINT ""FK_Shops_NpcTemplates_NpcTemplateId"" FOREIGN KEY (""NpcTemplateId"") REFERENCES ""NpcTemplates"" (""TemplateId"") ON DELETE RESTRICT
);");
			migrationBuilder.Sql(@"INSERT INTO ""Shops_new"" (""NpcTemplateId"", ""VendorName"", ""MaxSize"") SELECT ""NpcTemplateId"", ""VendorName"", ""MaxSize"" FROM ""Shops"";");
			migrationBuilder.Sql(@"DROP TABLE ""Shops"";");
			migrationBuilder.Sql(@"ALTER TABLE ""Shops_new"" RENAME TO ""Shops"";");

			// 3. Recreate the child against the new parent key and restore the parked rows.
			migrationBuilder.Sql(@"
CREATE TABLE ""ShopItems"" (
    ""ShopId"" INTEGER NOT NULL,
    ""Pos"" INTEGER NOT NULL,
    ""Count"" INTEGER NOT NULL,
    ""ItemTemplateId"" INTEGER NOT NULL,
    ""Stat356"" INTEGER NOT NULL,
    ""TailLevel"" INTEGER NOT NULL,
    CONSTRAINT ""PK_ShopItems"" PRIMARY KEY (""ShopId"", ""Pos""),
    CONSTRAINT ""FK_ShopItems_Items_ItemTemplateId"" FOREIGN KEY (""ItemTemplateId"") REFERENCES ""Items"" (""Id"") ON DELETE RESTRICT,
    CONSTRAINT ""FK_ShopItems_Shops_ShopId"" FOREIGN KEY (""ShopId"") REFERENCES ""Shops"" (""NpcTemplateId"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"INSERT INTO ""ShopItems"" SELECT ""ShopId"", ""Pos"", ""Count"", ""ItemTemplateId"", ""Stat356"", ""TailLevel"" FROM ""ShopItems_tmp"";");
			migrationBuilder.Sql(@"DROP TABLE ""ShopItems_tmp"";");
			migrationBuilder.Sql(@"CREATE INDEX ""IX_ShopItems_ItemTemplateId"" ON ""ShopItems"" (""ItemTemplateId"");");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.Sql(@"CREATE TABLE ""ShopItems_tmp"" AS SELECT ""ShopId"", ""Pos"", ""Count"", ""ItemTemplateId"", ""Stat356"", ""TailLevel"" FROM ""ShopItems"";");
			migrationBuilder.Sql(@"DROP TABLE ""ShopItems"";");
			migrationBuilder.Sql(@"
CREATE TABLE ""Shops_old"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Shops"" PRIMARY KEY AUTOINCREMENT,
    ""NpcTemplateId"" INTEGER NOT NULL,
    ""VendorName"" TEXT NOT NULL,
    ""MaxSize"" INTEGER NOT NULL,
    CONSTRAINT ""FK_Shops_NpcTemplates_NpcTemplateId"" FOREIGN KEY (""NpcTemplateId"") REFERENCES ""NpcTemplates"" (""TemplateId"") ON DELETE RESTRICT
);");
			migrationBuilder.Sql(@"INSERT INTO ""Shops_old"" (""Id"", ""NpcTemplateId"", ""VendorName"", ""MaxSize"") SELECT ""NpcTemplateId"", ""NpcTemplateId"", ""VendorName"", ""MaxSize"" FROM ""Shops"";");
			migrationBuilder.Sql(@"DROP TABLE ""Shops"";");
			migrationBuilder.Sql(@"ALTER TABLE ""Shops_old"" RENAME TO ""Shops"";");
			migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""IX_Shops_NpcTemplateId"" ON ""Shops"" (""NpcTemplateId"");");
			migrationBuilder.Sql(@"
CREATE TABLE ""ShopItems"" (
    ""ShopId"" INTEGER NOT NULL,
    ""Pos"" INTEGER NOT NULL,
    ""Count"" INTEGER NOT NULL,
    ""ItemTemplateId"" INTEGER NOT NULL,
    ""Stat356"" INTEGER NOT NULL,
    ""TailLevel"" INTEGER NOT NULL,
    CONSTRAINT ""PK_ShopItems"" PRIMARY KEY (""ShopId"", ""Pos""),
    CONSTRAINT ""FK_ShopItems_Items_ItemTemplateId"" FOREIGN KEY (""ItemTemplateId"") REFERENCES ""Items"" (""Id"") ON DELETE RESTRICT,
    CONSTRAINT ""FK_ShopItems_Shops_ShopId"" FOREIGN KEY (""ShopId"") REFERENCES ""Shops"" (""Id"") ON DELETE CASCADE
);");
			migrationBuilder.Sql(@"INSERT INTO ""ShopItems"" SELECT * FROM ""ShopItems_tmp"";");
			migrationBuilder.Sql(@"DROP TABLE ""ShopItems_tmp"";");
			migrationBuilder.Sql(@"CREATE INDEX ""IX_ShopItems_ItemTemplateId"" ON ""ShopItems"" (""ItemTemplateId"");");
		}
	}
}

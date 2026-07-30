using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class CriminalUntilAsDateTimeOffset : Migration
    {
        /// <inheritdoc />
        // Intentionally EMPTY. Character.CriminalUntil went DateTime? -> DateTimeOffset?, which is a CLR-type
        // change with no DDL behind it: SQLite stores both as TEXT, so there is nothing to alter. The migration is
        // kept rather than removed so the model snapshot keeps tracking the model - dropping it would leave the
        // snapshot claiming DateTime and fold this diff into whatever migration is scaffolded next.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}

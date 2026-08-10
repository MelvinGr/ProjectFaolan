using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <summary>
    ///     #783: <c>SpellStatEffects</c> keeps one row per ELEMENT rather than one per (stat, period), and carries the
    ///     range's upper endpoint beside its lower one.
    /// </summary>
    /// <remarks>
    ///     ⚠️ HAND-EDITED - do not re-scaffold. The <c>DELETE</c> is not scaffoldable and is load-bearing: the old key
    ///     collapsed several elements onto one row, so every existing row would take <c>Sequence = 0</c> and a spell
    ///     with more than one of them would violate the new primary key the moment SQLite rebuilt the table. The rows
    ///     are wholly RDB-derived (<c>RdbSpellImporter</c>) and are restored by the seed on a database with no game
    ///     content, or by <c>--only rdb-spells</c> on one that has it - so clearing them loses nothing that is not
    ///     re-derivable, while carrying them across would ship the very collapse this migration exists to undo.
    ///     <para>
    ///         EF's SQLite provider hoists a raw <c>Sql</c> above its deferred table rebuild, which is the order this
    ///         needs: the delete runs against the OLD table, and the rebuild then copies nothing.
    ///     </para>
    /// </remarks>
    public partial class SpellStatEffectRangeAndBands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM SpellStatEffects;");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects");

            migrationBuilder.AddColumn<uint>(
                name: "Sequence",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "MaxAmount",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxHighAmount",
                table: "SpellStatEffects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects",
                columns: new[] { "SpellId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "MaxAmount",
                table: "SpellStatEffects");

            migrationBuilder.DropColumn(
                name: "MaxHighAmount",
                table: "SpellStatEffects");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpellStatEffects",
                table: "SpellStatEffects",
                columns: new[] { "SpellId", "StatId", "PeriodCentiseconds" });
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class NamePatrolNodeTrailerSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "PathDirection1",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "PathDirection2",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "PathTrailerWord",
                table: "PatrolNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetRotationW",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetRotationX",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetRotationY",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetRotationZ",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetX",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetY",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "TargetZ",
                table: "PatrolNodes",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PathDirection1",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "PathDirection2",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "PathTrailerWord",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetRotationW",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetRotationX",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetRotationY",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetRotationZ",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetX",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetY",
                table: "PatrolNodes");

            migrationBuilder.DropColumn(
                name: "TargetZ",
                table: "PatrolNodes");
        }
    }
}

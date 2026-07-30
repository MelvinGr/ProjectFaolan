using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class LdbKeysAsDecryptionKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyText",
                table: "QuestTextRefs");

            migrationBuilder.DropColumn(
                name: "ObjectiveKey",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TrailingKey",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "AreaTitleKey",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "MapKey",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "RegionKey",
                table: "QuestAreas");

            migrationBuilder.AddColumn<uint>(
                name: "KeyText1",
                table: "QuestTextRefs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "KeyText2",
                table: "QuestTextRefs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "KeyText3",
                table: "QuestTextRefs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "KeyText4",
                table: "QuestTextRefs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ObjectiveKey1",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ObjectiveKey2",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ObjectiveKey3",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "ObjectiveKey4",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TrailingKey1",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TrailingKey2",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TrailingKey3",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "TrailingKey4",
                table: "QuestGoalDefinitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "AreaTitleKey1",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "AreaTitleKey2",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "AreaTitleKey3",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "AreaTitleKey4",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "MapKey1",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "MapKey2",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "MapKey3",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "MapKey4",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RegionKey1",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RegionKey2",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RegionKey3",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<uint>(
                name: "RegionKey4",
                table: "QuestAreas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeyText1",
                table: "QuestTextRefs");

            migrationBuilder.DropColumn(
                name: "KeyText2",
                table: "QuestTextRefs");

            migrationBuilder.DropColumn(
                name: "KeyText3",
                table: "QuestTextRefs");

            migrationBuilder.DropColumn(
                name: "KeyText4",
                table: "QuestTextRefs");

            migrationBuilder.DropColumn(
                name: "ObjectiveKey1",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "ObjectiveKey2",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "ObjectiveKey3",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "ObjectiveKey4",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TrailingKey1",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TrailingKey2",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TrailingKey3",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "TrailingKey4",
                table: "QuestGoalDefinitions");

            migrationBuilder.DropColumn(
                name: "AreaTitleKey1",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "AreaTitleKey2",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "AreaTitleKey3",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "AreaTitleKey4",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "MapKey1",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "MapKey2",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "MapKey3",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "MapKey4",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "RegionKey1",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "RegionKey2",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "RegionKey3",
                table: "QuestAreas");

            migrationBuilder.DropColumn(
                name: "RegionKey4",
                table: "QuestAreas");

            migrationBuilder.AddColumn<string>(
                name: "KeyText",
                table: "QuestTextRefs",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ObjectiveKey",
                table: "QuestGoalDefinitions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TrailingKey",
                table: "QuestGoalDefinitions",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AreaTitleKey",
                table: "QuestAreas",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MapKey",
                table: "QuestAreas",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegionKey",
                table: "QuestAreas",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }
    }
}

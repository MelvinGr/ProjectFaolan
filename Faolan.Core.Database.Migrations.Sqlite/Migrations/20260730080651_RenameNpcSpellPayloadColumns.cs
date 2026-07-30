using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Faolan.Core.Database.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class RenameNpcSpellPayloadColumns : Migration
    {
        /// <summary>
        /// Renames the ApplySpell payload columns to what the CLIENT reads (#480). Data-preserving: every value stays
        /// with its own byte offset.
        /// </summary>
        /// <remarks>
        /// ⚠️ Hand-corrected. The scaffolder paired the old and new names ALPHABETICALLY, not by meaning, and got three
        /// of the four backwards - Rank->DescriptorSlot8, FlagB->CastingLevel, FlagC->DescriptorSlot0. That version
        /// migrates and builds cleanly while silently moving @45's value into the @33 column and so on, which would
        /// rebuild every NPC cast payload with the bytes transposed. The correct mapping is offset-for-offset:
        ///   FlagB     @17 -> DescriptorSlot0
        ///   Magnitude @29 -> DescriptorSlot3
        ///   FlagC     @33 -> DescriptorSlot8
        ///   Rank      @45 -> CastingLevel
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "FlagB", table: "NpcSpells", newName: "DescriptorSlot0");
            migrationBuilder.RenameColumn(name: "Magnitude", table: "NpcSpells", newName: "DescriptorSlot3");
            migrationBuilder.RenameColumn(name: "FlagC", table: "NpcSpells", newName: "DescriptorSlot8");
            migrationBuilder.RenameColumn(name: "Rank", table: "NpcSpells", newName: "CastingLevel");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(name: "DescriptorSlot0", table: "NpcSpells", newName: "FlagB");
            migrationBuilder.RenameColumn(name: "DescriptorSlot3", table: "NpcSpells", newName: "Magnitude");
            migrationBuilder.RenameColumn(name: "DescriptorSlot8", table: "NpcSpells", newName: "FlagC");
            migrationBuilder.RenameColumn(name: "CastingLevel", table: "NpcSpells", newName: "Rank");
        }
    }
}

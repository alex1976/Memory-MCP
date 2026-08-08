using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MemoryMcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "memories",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memories_SpaceId_Category",
                table: "memories",
                columns: new[] { "SpaceId", "Category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memories_SpaceId_Category",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "memories");
        }
    }
}

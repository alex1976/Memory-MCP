using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MemoryMcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryEdges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_edges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToMemoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationType = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_edges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_memory_edges_memories_FromMemoryId",
                        column: x => x.FromMemoryId,
                        principalTable: "memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_edges_memories_ToMemoryId",
                        column: x => x.ToMemoryId,
                        principalTable: "memories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_memory_edges_spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_memory_edges_FromMemoryId",
                table: "memory_edges",
                column: "FromMemoryId");

            migrationBuilder.CreateIndex(
                name: "IX_memory_edges_SpaceId_FromMemoryId",
                table: "memory_edges",
                columns: new[] { "SpaceId", "FromMemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_edges_SpaceId_ToMemoryId",
                table: "memory_edges",
                columns: new[] { "SpaceId", "ToMemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_memory_edges_ToMemoryId",
                table: "memory_edges",
                column: "ToMemoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_edges");
        }
    }
}

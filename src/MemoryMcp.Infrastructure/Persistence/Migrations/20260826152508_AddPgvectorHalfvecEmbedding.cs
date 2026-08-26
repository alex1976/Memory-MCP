using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MemoryMcp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPgvectorHalfvecEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AlterColumn<HalfVector>(
                name: "Embedding",
                table: "memories",
                type: "halfvec(3072)",
                nullable: true,
                oldClrType: typeof(float[]),
                oldType: "real[]",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memories_Embedding",
                table: "memories",
                column: "Embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "halfvec_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_memories_Embedding",
                table: "memories");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AlterColumn<float[]>(
                name: "Embedding",
                table: "memories",
                type: "real[]",
                nullable: true,
                oldClrType: typeof(HalfVector),
                oldType: "halfvec(3072)",
                oldNullable: true);
        }
    }
}

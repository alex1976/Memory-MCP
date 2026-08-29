using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MemoryMcp.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Introduces <c>users</c> and makes every credential and every write attributable to one.
    /// </summary>
    /// <remarks>
    /// The scaffolded version of this migration added <c>api_keys.UserId</c> as NOT NULL with an
    /// all-zeros default, which would have pointed every existing key at a user that does not exist and
    /// failed the foreign key. The order below is therefore hand-written: create the table, add the
    /// column nullable, derive one user per distinct owner from the data already in <c>api_keys</c>
    /// (<c>OwnerEmail</c> is read here and dropped afterwards, since <c>users.Email</c> supersedes it),
    /// only then tighten the column and add the constraints.
    ///
    /// Existing memories and documents keep NULL authorship: there is no record of who wrote them, and
    /// inventing one would be worse than admitting it. Backfilled users are created as
    /// <c>Writer</c> so keys that could write yesterday can still write today.
    /// </remarks>
    public partial class AddUsersAndWriteAttribution : Migration
    {
        /// <summary>Derives the identity of a pre-users key's owner. Keys that carried an OwnerEmail keep
        /// it (two keys with the same email are correctly recognized as the same person); keys that
        /// carried none get a synthetic address built from the key prefix.
        /// <para>The prefix is the first 12 characters of the generated key, so it is effectively unique
        /// for keys minted by the app — but nothing enforces that, and keys created by hand or by test
        /// fixtures can share one. Such keys collapse into a single synthetic user. That is accepted
        /// rather than worked around: an unattributed pre-users key has, by definition, authored no row
        /// that names it, so over-merging their identities loses no information. The alternative — one
        /// user per key id — would invent a separate person for every credential a real user held.</para></summary>
        private const string LegacyOwnerEmailSql =
            "lower(coalesce(nullif(k.\"OwnerEmail\", ''), k.\"KeyPrefix\" || '@legacy.memory-mcp.local'))";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            // One user per distinct owner, not per key: a person holding a laptop key and a CI key must
            // end up as one user, otherwise their memories would look like two people's.
            migrationBuilder.Sql($"""
                INSERT INTO users ("Id", "Email", "DisplayName", "Role", "IsActive", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), legacy_owner.email, legacy_owner.display_name, 'Writer', TRUE, now(), now()
                FROM (
                    SELECT {LegacyOwnerEmailSql} AS email,
                           min(coalesce(nullif(k."Label", ''), k."KeyPrefix")) AS display_name
                    FROM api_keys k
                    GROUP BY 1
                ) AS legacy_owner
                ON CONFLICT ("Email") DO NOTHING;
                """);

            migrationBuilder.Sql($"""
                UPDATE api_keys k
                SET "UserId" = u."Id"
                FROM users u
                WHERE u."Email" = {LegacyOwnerEmailSql};
                """);

            // oldNullable is what actually produces the SET NOT NULL: the generator diffs the operation
            // against the old column it is told about, and without it the column silently stays nullable.
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                table: "api_keys",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "api_keys");

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "memories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UpdatedByUserId",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_memories_CreatedByUserId",
                table: "memories",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_memories_UpdatedByUserId",
                table: "memories",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_CreatedByUserId",
                table: "documents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_documents_UpdatedByUserId",
                table: "documents",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_UserId",
                table: "api_keys",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_users_UserId",
                table: "api_keys",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_users_CreatedByUserId",
                table: "documents",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_documents_users_UpdatedByUserId",
                table: "documents",
                column: "UpdatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_memories_users_CreatedByUserId",
                table: "memories",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_memories_users_UpdatedByUserId",
                table: "memories",
                column: "UpdatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_api_keys_users_UserId",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "FK_documents_users_CreatedByUserId",
                table: "documents");

            migrationBuilder.DropForeignKey(
                name: "FK_documents_users_UpdatedByUserId",
                table: "documents");

            migrationBuilder.DropForeignKey(
                name: "FK_memories_users_CreatedByUserId",
                table: "memories");

            migrationBuilder.DropForeignKey(
                name: "FK_memories_users_UpdatedByUserId",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "IX_memories_CreatedByUserId",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "IX_memories_UpdatedByUserId",
                table: "memories");

            migrationBuilder.DropIndex(
                name: "IX_documents_CreatedByUserId",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_documents_UpdatedByUserId",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "IX_api_keys_UserId",
                table: "api_keys");

            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "api_keys",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            // Restores what the Up backfill read, so a down-then-up round trip lands on the same users.
            // Synthetic legacy addresses are deliberately not restored: they were derived from KeyPrefix
            // and re-deriving them on the next Up gives the same result.
            migrationBuilder.Sql("""
                UPDATE api_keys k
                SET "OwnerEmail" = u."Email"
                FROM users u
                WHERE u."Id" = k."UserId" AND u."Email" NOT LIKE '%@legacy.memory-mcp.local';
                """);

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "memories");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "documents");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}

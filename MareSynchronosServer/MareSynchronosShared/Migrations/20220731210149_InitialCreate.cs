using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BannedUsers",
                columns: table => new
                {
                    character_identification = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_banned_users", x => x.character_identification);
                });

            migrationBuilder.CreateTable(
                name: "ForbiddenUploadEntries",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    forbidden_by = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forbidden_upload_entries", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    character_identification = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    is_moderator = table.Column<bool>(type: "boolean", nullable: false),
                    is_admin = table.Column<bool>(type: "boolean", nullable: false),
                    last_logged_in = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.uid);
                });

            migrationBuilder.CreateTable(
                name: "Auth",
                columns: table => new
                {
                    hashed_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth", x => x.hashed_key);
                    table.ForeignKey(
                        name: "fk_auth_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "Users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateTable(
                name: "ClientPairs",
                columns: table => new
                {
                    user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    other_user_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    allow_receiving_messages = table.Column<bool>(type: "boolean", nullable: false),
                    timestamp = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_pairs", x => new { x.user_uid, x.other_user_uid });
                    table.ForeignKey(
                        name: "fk_client_pairs_users_other_user_uid",
                        column: x => x.other_user_uid,
                        principalTable: "Users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_client_pairs_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "Users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileCaches",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    uploader_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    uploaded = table.Column<bool>(type: "boolean", nullable: false),
                    timestamp = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_caches", x => x.hash);
                    table.ForeignKey(
                        name: "fk_file_caches_users_uploader_uid",
                        column: x => x.uploader_uid,
                        principalTable: "Users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_user_uid",
                table: "Auth",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_client_pairs_other_user_uid",
                table: "ClientPairs",
                column: "other_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_client_pairs_user_uid",
                table: "ClientPairs",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_file_caches_uploader_uid",
                table: "FileCaches",
                column: "uploader_uid");

            migrationBuilder.CreateIndex(
                name: "ix_users_character_identification",
                table: "Users",
                column: "character_identification");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Auth");

            migrationBuilder.DropTable(
                name: "BannedUsers");

            migrationBuilder.DropTable(
                name: "ClientPairs");

            migrationBuilder.DropTable(
                name: "FileCaches");

            migrationBuilder.DropTable(
                name: "ForbiddenUploadEntries");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}

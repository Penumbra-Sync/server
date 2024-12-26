using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class CharaData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chara_data",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    uploader_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    created_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    access_type = table.Column<int>(type: "integer", nullable: false),
                    share_type = table.Column<int>(type: "integer", nullable: false),
                    expiry_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    glamourer_data = table.Column<string>(type: "text", nullable: true),
                    customize_data = table.Column<string>(type: "text", nullable: true),
                    download_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data", x => new { x.id, x.uploader_uid });
                    table.ForeignKey(
                        name: "fk_chara_data_users_uploader_uid",
                        column: x => x.uploader_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chara_data_allowance",
                columns: table => new
                {
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    allowed_user_uid = table.Column<string>(type: "character varying(10)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_allowance", x => new { x.parent_id, x.parent_uploader_uid, x.allowed_user_uid });
                    table.ForeignKey(
                        name: "fk_chara_data_allowance_chara_data_parent_id_parent_uploader_u",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chara_data_allowance_users_allowed_user_uid",
                        column: x => x.allowed_user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chara_data_files",
                columns: table => new
                {
                    game_path = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    file_cache_hash = table.Column<string>(type: "character varying(40)", nullable: true),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_files", x => new { x.parent_id, x.game_path });
                    table.ForeignKey(
                        name: "fk_chara_data_files_chara_data_parent_id_parent_uploader_uid",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" });
                    table.ForeignKey(
                        name: "fk_chara_data_files_files_file_cache_hash",
                        column: x => x.file_cache_hash,
                        principalTable: "file_caches",
                        principalColumn: "hash");
                });

            migrationBuilder.CreateTable(
                name: "chara_data_orig_files",
                columns: table => new
                {
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_orig_files", x => new { x.parent_id, x.parent_uploader_uid, x.hash });
                    table.ForeignKey(
                        name: "fk_chara_data_orig_files_chara_data_parent_id_parent_uploader_",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chara_data_poses",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    pose_data = table.Column<string>(type: "text", nullable: true),
                    world_data = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_poses", x => new { x.parent_id, x.parent_uploader_uid, x.id });
                    table.ForeignKey(
                        name: "fk_chara_data_poses_chara_data_parent_id_parent_uploader_uid",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_id",
                table: "chara_data",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_uploader_uid",
                table: "chara_data",
                column: "uploader_uid");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_allowance_allowed_user_uid",
                table: "chara_data_allowance",
                column: "allowed_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_allowance_parent_id",
                table: "chara_data_allowance",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_files_file_cache_hash",
                table: "chara_data_files",
                column: "file_cache_hash");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_files_parent_id",
                table: "chara_data_files",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_files_parent_id_parent_uploader_uid",
                table: "chara_data_files",
                columns: new[] { "parent_id", "parent_uploader_uid" });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_orig_files_parent_id",
                table: "chara_data_orig_files",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_poses_parent_id",
                table: "chara_data_poses",
                column: "parent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chara_data_allowance");

            migrationBuilder.DropTable(
                name: "chara_data_files");

            migrationBuilder.DropTable(
                name: "chara_data_orig_files");

            migrationBuilder.DropTable(
                name: "chara_data_poses");

            migrationBuilder.DropTable(
                name: "chara_data");
        }
    }
}

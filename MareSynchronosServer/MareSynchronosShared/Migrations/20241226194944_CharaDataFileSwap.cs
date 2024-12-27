using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class CharaDataFileSwap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chara_data_files_chara_data_parent_id_parent_uploader_uid",
                table: "chara_data_files");

            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_files",
                table: "chara_data_files");

            migrationBuilder.DropIndex(
                name: "ix_chara_data_files_parent_id_parent_uploader_uid",
                table: "chara_data_files");

            migrationBuilder.AlterColumn<string>(
                name: "parent_uploader_uid",
                table: "chara_data_files",
                type: "character varying(10)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_files",
                table: "chara_data_files",
                columns: new[] { "parent_id", "parent_uploader_uid", "game_path" });

            migrationBuilder.CreateTable(
                name: "chara_data_file_swaps",
                columns: table => new
                {
                    parent_id = table.Column<string>(type: "text", nullable: false),
                    parent_uploader_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    game_path = table.Column<string>(type: "text", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chara_data_file_swaps", x => new { x.parent_id, x.parent_uploader_uid, x.game_path });
                    table.ForeignKey(
                        name: "fk_chara_data_file_swaps_chara_data_parent_id_parent_uploader_",
                        columns: x => new { x.parent_id, x.parent_uploader_uid },
                        principalTable: "chara_data",
                        principalColumns: new[] { "id", "uploader_uid" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_file_swaps_parent_id",
                table: "chara_data_file_swaps",
                column: "parent_id");

            migrationBuilder.AddForeignKey(
                name: "fk_chara_data_files_chara_data_parent_id_parent_uploader_uid",
                table: "chara_data_files",
                columns: new[] { "parent_id", "parent_uploader_uid" },
                principalTable: "chara_data",
                principalColumns: new[] { "id", "uploader_uid" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chara_data_files_chara_data_parent_id_parent_uploader_uid",
                table: "chara_data_files");

            migrationBuilder.DropTable(
                name: "chara_data_file_swaps");

            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_files",
                table: "chara_data_files");

            migrationBuilder.AlterColumn<string>(
                name: "parent_uploader_uid",
                table: "chara_data_files",
                type: "character varying(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)");

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_files",
                table: "chara_data_files",
                columns: new[] { "parent_id", "game_path" });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_files_parent_id_parent_uploader_uid",
                table: "chara_data_files",
                columns: new[] { "parent_id", "parent_uploader_uid" });

            migrationBuilder.AddForeignKey(
                name: "fk_chara_data_files_chara_data_parent_id_parent_uploader_uid",
                table: "chara_data_files",
                columns: new[] { "parent_id", "parent_uploader_uid" },
                principalTable: "chara_data",
                principalColumns: new[] { "id", "uploader_uid" });
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class OrigFileGamePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_orig_files",
                table: "chara_data_orig_files");

            migrationBuilder.AlterColumn<string>(
                name: "hash",
                table: "chara_data_orig_files",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "game_path",
                table: "chara_data_orig_files",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_orig_files",
                table: "chara_data_orig_files",
                columns: new[] { "parent_id", "parent_uploader_uid", "game_path" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_orig_files",
                table: "chara_data_orig_files");

            migrationBuilder.DropColumn(
                name: "game_path",
                table: "chara_data_orig_files");

            migrationBuilder.AlterColumn<string>(
                name: "hash",
                table: "chara_data_orig_files",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_orig_files",
                table: "chara_data_orig_files",
                columns: new[] { "parent_id", "parent_uploader_uid", "hash" });
        }
    }
}

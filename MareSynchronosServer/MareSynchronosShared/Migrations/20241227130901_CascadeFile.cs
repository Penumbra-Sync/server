using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class CascadeFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chara_data_files_files_file_cache_hash",
                table: "chara_data_files");

            migrationBuilder.AddForeignKey(
                name: "fk_chara_data_files_files_file_cache_hash",
                table: "chara_data_files",
                column: "file_cache_hash",
                principalTable: "file_caches",
                principalColumn: "hash",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chara_data_files_files_file_cache_hash",
                table: "chara_data_files");

            migrationBuilder.AddForeignKey(
                name: "fk_chara_data_files_files_file_cache_hash",
                table: "chara_data_files",
                column: "file_cache_hash",
                principalTable: "file_caches",
                principalColumn: "hash");
        }
    }
}

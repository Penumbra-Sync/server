using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class RenameLowerSnakeCase : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_user_uid",
                table: "Auth");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_other_user_uid",
                table: "ClientPairs");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_user_uid",
                table: "ClientPairs");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "users");

            migrationBuilder.RenameTable(
                name: "Auth",
                newName: "auth");

            migrationBuilder.RenameTable(
                name: "ForbiddenUploadEntries",
                newName: "forbidden_upload_entries");

            migrationBuilder.RenameTable(
                name: "FileCaches",
                newName: "file_caches");

            migrationBuilder.RenameTable(
                name: "ClientPairs",
                newName: "client_pairs");

            migrationBuilder.RenameTable(
                name: "BannedUsers",
                newName: "banned_users");

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_user_temp_id",
                table: "auth",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_other_user_temp_id1",
                table: "client_pairs",
                column: "other_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_user_temp_id2",
                table: "client_pairs",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_user_temp_id",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_other_user_temp_id1",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_user_temp_id2",
                table: "client_pairs");

            migrationBuilder.RenameTable(
                name: "users",
                newName: "Users");

            migrationBuilder.RenameTable(
                name: "auth",
                newName: "Auth");

            migrationBuilder.RenameTable(
                name: "forbidden_upload_entries",
                newName: "ForbiddenUploadEntries");

            migrationBuilder.RenameTable(
                name: "file_caches",
                newName: "FileCaches");

            migrationBuilder.RenameTable(
                name: "client_pairs",
                newName: "ClientPairs");

            migrationBuilder.RenameTable(
                name: "banned_users",
                newName: "BannedUsers");

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_user_uid",
                table: "Auth",
                column: "user_uid",
                principalTable: "Users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_other_user_uid",
                table: "ClientPairs",
                column: "other_user_uid",
                principalTable: "Users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_user_uid",
                table: "ClientPairs",
                column: "user_uid",
                principalTable: "Users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

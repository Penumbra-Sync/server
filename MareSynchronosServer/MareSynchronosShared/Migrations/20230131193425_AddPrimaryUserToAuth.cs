using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryUserToAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_by_temp_id4",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_user_temp_id5",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id6",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id7",
                table: "groups");

            migrationBuilder.AddColumn<string>(
                name: "primary_user_uid",
                table: "auth",
                type: "character varying(10)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_primary_user_uid",
                table: "auth",
                column: "primary_user_uid");

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_primary_user_temp_id",
                table: "auth",
                column: "primary_user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_user_temp_id1",
                table: "auth",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_other_user_temp_id2",
                table: "client_pairs",
                column: "other_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_user_temp_id3",
                table: "client_pairs",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_by_temp_id5",
                table: "group_bans",
                column: "banned_by_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_user_temp_id6",
                table: "group_bans",
                column: "banned_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id7",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id8",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_primary_user_temp_id",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_user_temp_id1",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_other_user_temp_id2",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_user_temp_id3",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_by_temp_id5",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_user_temp_id6",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id7",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id8",
                table: "groups");

            migrationBuilder.DropIndex(
                name: "ix_auth_primary_user_uid",
                table: "auth");

            migrationBuilder.DropColumn(
                name: "primary_user_uid",
                table: "auth");

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

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_by_temp_id4",
                table: "group_bans",
                column: "banned_by_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_user_temp_id5",
                table: "group_bans",
                column: "banned_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id6",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id7",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }
    }
}

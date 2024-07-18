using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class MarkForBan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                name: "fk_group_bans_groups_group_temp_id",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_by_temp_id5",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_user_temp_id6",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pair_preferred_permissions_groups_group_temp_id1",
                table: "group_pair_preferred_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pair_preferred_permissions_users_user_temp_id7",
                table: "group_pair_preferred_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_temp_id2",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id8",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id9",
                table: "groups");

            migrationBuilder.AddColumn<bool>(
                name: "mark_for_ban",
                table: "auth",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_primary_user_uid",
                table: "auth",
                column: "primary_user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_auth_users_user_uid",
                table: "auth",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_other_user_uid",
                table: "client_pairs",
                column: "other_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_client_pairs_users_user_uid",
                table: "client_pairs",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_groups_group_gid",
                table: "group_bans",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_by_uid",
                table: "group_bans",
                column: "banned_by_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.AddForeignKey(
                name: "fk_group_bans_users_banned_user_uid",
                table: "group_bans",
                column: "banned_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pair_preferred_permissions_groups_group_gid",
                table: "group_pair_preferred_permissions",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pair_preferred_permissions_users_user_uid",
                table: "group_pair_preferred_permissions",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_gid",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_uid",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_uid",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_primary_user_uid",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_user_uid",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_other_user_uid",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_user_uid",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_groups_group_gid",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_by_uid",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_bans_users_banned_user_uid",
                table: "group_bans");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pair_preferred_permissions_groups_group_gid",
                table: "group_pair_preferred_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pair_preferred_permissions_users_user_uid",
                table: "group_pair_preferred_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_gid",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_uid",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_uid",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "mark_for_ban",
                table: "auth");

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
                name: "fk_group_bans_groups_group_temp_id",
                table: "group_bans",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
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
                name: "fk_group_pair_preferred_permissions_groups_group_temp_id1",
                table: "group_pair_preferred_permissions",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pair_preferred_permissions_users_user_temp_id7",
                table: "group_pair_preferred_permissions",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_temp_id2",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id8",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id9",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }
    }
}

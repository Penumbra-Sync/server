using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class groupbans : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_temp_id",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id4",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id5",
                table: "groups");

            migrationBuilder.CreateTable(
                name: "group_bans",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: false),
                    banned_user_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    banned_by_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    banned_on = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    banned_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_bans", x => new { x.group_gid, x.banned_user_uid });
                    table.ForeignKey(
                        name: "fk_group_bans_groups_group_temp_id",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_bans_users_banned_by_temp_id4",
                        column: x => x.banned_by_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                    table.ForeignKey(
                        name: "fk_group_bans_users_banned_user_temp_id5",
                        column: x => x.banned_user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_bans_banned_by_uid",
                table: "group_bans",
                column: "banned_by_uid");

            migrationBuilder.CreateIndex(
                name: "ix_group_bans_banned_user_uid",
                table: "group_bans",
                column: "banned_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_group_bans_group_gid",
                table: "group_bans",
                column: "group_gid");

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_temp_id1",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
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

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_temp_id1",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id6",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id7",
                table: "groups");

            migrationBuilder.DropTable(
                name: "group_bans");

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_temp_id",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id4",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id5",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }
    }
}

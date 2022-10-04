using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class Groups : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_character_identification",
                table: "users");

            migrationBuilder.DropColumn(
                name: "character_identification",
                table: "users");

            migrationBuilder.AlterColumn<string>(
                name: "alias",
                table: "users",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    gid = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    owner_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    alias = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    invites_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    hashed_password = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.gid);
                    table.ForeignKey(
                        name: "fk_groups_users_owner_temp_id5",
                        column: x => x.owner_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateTable(
                name: "group_pairs",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(14)", nullable: false),
                    group_user_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_pairs", x => new { x.group_gid, x.group_user_uid });
                    table.ForeignKey(
                        name: "fk_group_pairs_groups_group_temp_id",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_pairs_users_group_user_temp_id4",
                        column: x => x.group_user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_pairs_group_gid",
                table: "group_pairs",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_group_pairs_group_user_uid",
                table: "group_pairs",
                column: "group_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_groups_owner_uid",
                table: "groups",
                column: "owner_uid");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_pairs");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.AlterColumn<string>(
                name: "alias",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "character_identification",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_character_identification",
                table: "users",
                column: "character_identification");
        }
    }
}

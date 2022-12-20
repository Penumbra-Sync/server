using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class GroupTempInvite : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_temp_invites",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: false),
                    invite = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    expiration_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_temp_invites", x => new { x.group_gid, x.invite });
                    table.ForeignKey(
                        name: "fk_group_temp_invites_groups_group_gid",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_temp_invites_group_gid",
                table: "group_temp_invites",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_group_temp_invites_invite",
                table: "group_temp_invites",
                column: "invite");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_temp_invites");
        }
    }
}

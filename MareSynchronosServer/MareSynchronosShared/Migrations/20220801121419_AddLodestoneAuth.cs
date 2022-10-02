using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class AddLodestoneAuth : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lodestone_auth",
                columns: table => new
                {
                    discord_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    hashed_lodestone_id = table.Column<string>(type: "text", nullable: true),
                    lodestone_auth_string = table.Column<string>(type: "text", nullable: true),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lodestone_auth", x => x.discord_id);
                    table.ForeignKey(
                        name: "fk_lodestone_auth_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_lodestone_auth_user_uid",
                table: "lodestone_auth",
                column: "user_uid");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lodestone_auth");
        }
    }
}

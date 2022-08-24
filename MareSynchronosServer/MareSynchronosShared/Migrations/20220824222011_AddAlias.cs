using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class AddAlias : Migration
    {
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

            migrationBuilder.CreateTable(
                name: "aliases",
                columns: table => new
                {
                    alias_uid = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_aliases", x => x.alias_uid);
                    table.ForeignKey(
                        name: "fk_aliases_users_user_temp_id",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_aliases_alias_uid",
                table: "aliases",
                column: "alias_uid");

            migrationBuilder.CreateIndex(
                name: "ix_aliases_user_uid",
                table: "aliases",
                column: "user_uid");

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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_auth_users_user_temp_id1",
                table: "auth");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_other_user_temp_id2",
                table: "client_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_client_pairs_users_user_temp_id3",
                table: "client_pairs");

            migrationBuilder.DropTable(
                name: "aliases");

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
    }
}

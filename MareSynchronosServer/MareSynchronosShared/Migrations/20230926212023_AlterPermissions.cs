using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AlterPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_default_preferred_permissions_users_user_temp_id13",
                table: "user_default_preferred_permissions");

            migrationBuilder.DropIndex(
                name: "ix_user_default_preferred_permissions_user_uid1",
                table: "user_default_preferred_permissions");

            migrationBuilder.DropColumn(
                name: "user_uid1",
                table: "user_default_preferred_permissions");

            migrationBuilder.AlterColumn<string>(
                name: "user_uid",
                table: "user_default_preferred_permissions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateIndex(
                name: "ix_user_default_preferred_permissions_user_uid",
                table: "user_default_preferred_permissions",
                column: "user_uid");

            migrationBuilder.AddForeignKey(
                name: "fk_user_default_preferred_permissions_users_user_uid",
                table: "user_default_preferred_permissions",
                column: "user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_user_default_preferred_permissions_users_user_uid",
                table: "user_default_preferred_permissions");

            migrationBuilder.DropIndex(
                name: "ix_user_default_preferred_permissions_user_uid",
                table: "user_default_preferred_permissions");

            migrationBuilder.AlterColumn<string>(
                name: "user_uid",
                table: "user_default_preferred_permissions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<string>(
                name: "user_uid1",
                table: "user_default_preferred_permissions",
                type: "character varying(10)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_default_preferred_permissions_user_uid1",
                table: "user_default_preferred_permissions",
                column: "user_uid1");

            migrationBuilder.AddForeignKey(
                name: "fk_user_default_preferred_permissions_users_user_temp_id13",
                table: "user_default_preferred_permissions",
                column: "user_uid1",
                principalTable: "users",
                principalColumn: "uid");
        }
    }
}

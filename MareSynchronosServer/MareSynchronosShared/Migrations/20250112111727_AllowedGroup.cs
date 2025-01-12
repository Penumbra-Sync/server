using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class AllowedGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_allowance",
                table: "chara_data_allowance");

            migrationBuilder.AlterColumn<string>(
                name: "allowed_user_uid",
                table: "chara_data_allowance",
                type: "character varying(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)");

            migrationBuilder.AddColumn<long>(
                name: "id",
                table: "chara_data_allowance",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddColumn<string>(
                name: "allowed_group_gid",
                table: "chara_data_allowance",
                type: "character varying(20)",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_allowance",
                table: "chara_data_allowance",
                columns: new[] { "parent_id", "parent_uploader_uid", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_chara_data_allowance_allowed_group_gid",
                table: "chara_data_allowance",
                column: "allowed_group_gid");

            migrationBuilder.AddForeignKey(
                name: "fk_chara_data_allowance_groups_allowed_group_gid",
                table: "chara_data_allowance",
                column: "allowed_group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_chara_data_allowance_groups_allowed_group_gid",
                table: "chara_data_allowance");

            migrationBuilder.DropPrimaryKey(
                name: "pk_chara_data_allowance",
                table: "chara_data_allowance");

            migrationBuilder.DropIndex(
                name: "ix_chara_data_allowance_allowed_group_gid",
                table: "chara_data_allowance");

            migrationBuilder.DropColumn(
                name: "id",
                table: "chara_data_allowance");

            migrationBuilder.DropColumn(
                name: "allowed_group_gid",
                table: "chara_data_allowance");

            migrationBuilder.AlterColumn<string>(
                name: "allowed_user_uid",
                table: "chara_data_allowance",
                type: "character varying(10)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_chara_data_allowance",
                table: "chara_data_allowance",
                columns: new[] { "parent_id", "parent_uploader_uid", "allowed_user_uid" });
        }
    }
}

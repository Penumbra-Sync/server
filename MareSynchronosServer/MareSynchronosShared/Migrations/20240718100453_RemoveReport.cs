using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_profile_data_reports");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_profile_data_reports",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reported_user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    reporting_user_uid = table.Column<string>(type: "character varying(10)", nullable: true),
                    report_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    report_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profile_data_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_profile_data_reports_users_reported_user_uid",
                        column: x => x.reported_user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                    table.ForeignKey(
                        name: "fk_user_profile_data_reports_users_reporting_user_uid",
                        column: x => x.reporting_user_uid,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_data_reports_reported_user_uid",
                table: "user_profile_data_reports",
                column: "reported_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_profile_data_reports_reporting_user_uid",
                table: "user_profile_data_reports",
                column: "reporting_user_uid");
        }
    }
}

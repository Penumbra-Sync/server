using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class SetMaxLengths : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAccessTime",
                table: "FileCaches");

            migrationBuilder.DropForeignKey("FK_FileCaches_Users_UploaderUID", "FileCaches");
            migrationBuilder.DropForeignKey("FK_ClientPairs_Users_UserUID", "ClientPairs");
            migrationBuilder.DropForeignKey("FK_ClientPairs_Users_OtherUserUID", "ClientPairs");
            migrationBuilder.DropPrimaryKey("PK_FileCaches", "FileCaches");
            migrationBuilder.DropPrimaryKey("PK_Users", "Users");

            migrationBuilder.AlterColumn<string>(
                name: "UploaderUID",
                table: "FileCaches",
                type: "nvarchar(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Hash",
                table: "FileCaches",
                type: "nvarchar(40)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AlterColumn<string>(
                name: "UserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OtherUserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UID",
                table: "Users",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.AddPrimaryKey("PK_Users", "Users", "UID");
            migrationBuilder.AddPrimaryKey("PK_FileCaches", "FileCaches", "Hash");
            migrationBuilder.AddForeignKey("FK_FileCaches_Users_UploaderUID", "FileCaches", "UploaderUID", "Users");
            migrationBuilder.AddForeignKey("FK_ClientPairs_Users_UserUID", "ClientPairs", "UserUID", "Users");
            migrationBuilder.AddForeignKey("FK_ClientPairs_Users_OtherUserUID", "ClientPairs", "UserUID", "Users");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_FileCaches_Users_UploaderUID", "FileCaches");
            migrationBuilder.DropForeignKey("FK_ClientPairs_Users_UserUID", "ClientPairs");
            migrationBuilder.DropForeignKey("FK_ClientPairs_Users_OtherUserUID", "ClientPairs");
            migrationBuilder.DropPrimaryKey("PK_FileCaches", "FileCaches");
            migrationBuilder.DropPrimaryKey("PK_Users", "Users");

            migrationBuilder.AlterColumn<string>(
                name: "UID",
                table: "Users",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "UploaderUID",
                table: "FileCaches",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Hash",
                table: "FileCaches",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAccessTime",
                table: "FileCaches",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<string>(
                name: "UserUID",
                table: "ClientPairs",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OtherUserUID",
                table: "ClientPairs",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey("PK_Users", "Users", "UID");
            migrationBuilder.AddPrimaryKey("PK_FileCaches", "FileCaches", "Hash");
            migrationBuilder.AddForeignKey("FK_FileCaches_Users_UploaderUID", "FileCaches", "UploaderUID", "Users");
            migrationBuilder.AddForeignKey("FK_ClientPairs_Users_UserUID", "ClientPairs", "UserUID", "Users");
            migrationBuilder.AddForeignKey("FK_ClientPairs_Users_OtherUserUID", "ClientPairs", "UserUID", "Users");
        }
    }
}

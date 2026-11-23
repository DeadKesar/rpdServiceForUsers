using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class EmailChangeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_change_requests",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    new_email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    old_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    new_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    old_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    new_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_email_change_requests", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_email_change_requests_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_email_change_requests_new_token_hash",
                table: "email_change_requests",
                column: "new_token_hash");

            migrationBuilder.CreateIndex(
                name: "ix_email_change_requests_old_token_hash",
                table: "email_change_requests",
                column: "old_token_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_change_requests");
        }
    }
}

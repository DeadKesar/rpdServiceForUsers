using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_signatures",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    resolver = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    resolved_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_signatures", x => x.id);
                    table.CheckConstraint("ck_document_signatures_approved", "(status = 'approved') = (approved_at IS NOT NULL)");
                    table.CheckConstraint("ck_document_signatures_rejected", "status <> 'rejected' OR comment IS NOT NULL");
                    table.CheckConstraint("ck_document_signatures_signatory", "status = 'pending' OR resolved_user_id IS NOT NULL");
                    table.CheckConstraint("ck_document_signatures_status", "status IN ('pending', 'approved', 'rejected')");
                    table.ForeignKey(
                        name: "fk_document_signatures_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_document_signatures_employees_resolved_user_id",
                        column: x => x.resolved_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    emailed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_users_recipient_id",
                        column: x => x.recipient_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_signatures_document_id_role",
                table: "document_signatures",
                columns: new[] { "document_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_signatures_document_id_status",
                table: "document_signatures",
                columns: new[] { "document_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_document_signatures_resolved_user_id_status",
                table: "document_signatures",
                columns: new[] { "resolved_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_document_id",
                table: "notifications",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id_created_at",
                table: "notifications",
                columns: new[] { "recipient_id", "created_at" },
                filter: "read_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_signatures");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}

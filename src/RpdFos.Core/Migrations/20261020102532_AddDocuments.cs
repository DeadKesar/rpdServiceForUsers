using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_outdated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_revision = table.Column<int>(type: "integer", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.CheckConstraint("ck_documents_deleted", "is_deleted = (status = 'deleted')");
                    table.CheckConstraint("ck_documents_status", "status IN ('draft', 'saved_draft', 'under_review', 'returned', 'accepted', 'approval', 'approved', 'archived', 'deleted')");
                    table.ForeignKey(
                        name: "fk_documents_disciplines_discipline_id",
                        column: x => x.discipline_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_document_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "document_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_employees_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_competence_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    indicator_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_competence_links", x => x.id);
                    table.CheckConstraint("ck_document_competence_links_target", "competence_id IS NOT NULL OR indicator_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_document_competence_links_competence_indicators_indicator_id",
                        column: x => x.indicator_id,
                        principalTable: "competence_indicators",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_competence_links_competences_competence_id",
                        column: x => x.competence_id,
                        principalTable: "competences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_competence_links_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester = table.Column<int>(type: "integer", nullable: false),
                    lectures = table.Column<int>(type: "integer", nullable: false),
                    practice = table.Column<int>(type: "integer", nullable: false),
                    lab_hours = table.Column<int>(type: "integer", nullable: false),
                    self_study = table.Column<int>(type: "integer", nullable: false),
                    control = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_hours", x => x.id);
                    table.CheckConstraint("ck_document_hours_non_negative", "lectures >= 0 AND practice >= 0 AND lab_hours >= 0 AND self_study >= 0 AND control >= 0");
                    table.ForeignKey(
                        name: "fk_document_hours_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_status_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    comment = table.Column<string>(type: "text", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_status_history_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_document_status_history_employees_changed_by",
                        column: x => x.changed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    hours_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    competences_snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    saved_by = table.Column<Guid>(type: "uuid", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trigger_event = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_versions_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_document_versions_employees_saved_by",
                        column: x => x.saved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_competence_links_competence_id",
                table: "document_competence_links",
                column: "competence_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_competence_links_document_id_competence_id_indicat",
                table: "document_competence_links",
                columns: new[] { "document_id", "competence_id", "indicator_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_document_competence_links_indicator_id",
                table: "document_competence_links",
                column: "indicator_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_hours_document_id_semester",
                table: "document_hours",
                columns: new[] { "document_id", "semester" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_status_history_changed_by",
                table: "document_status_history",
                column: "changed_by");

            migrationBuilder.CreateIndex(
                name: "ix_document_status_history_document_id_changed_at",
                table: "document_status_history",
                columns: new[] { "document_id", "changed_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_document_versions_document_id_version_number",
                table: "document_versions",
                columns: new[] { "document_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_versions_saved_by",
                table: "document_versions",
                column: "saved_by");

            migrationBuilder.CreateIndex(
                name: "ix_documents_author_id_status",
                table: "documents",
                columns: new[] { "author_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_discipline_id",
                table: "documents",
                column: "discipline_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_is_outdated",
                table: "documents",
                column: "is_outdated",
                filter: "is_outdated");

            migrationBuilder.CreateIndex(
                name: "ix_documents_payload",
                table: "documents",
                column: "payload")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_documents_status_updated_at",
                table: "documents",
                columns: new[] { "status", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_documents_template_id",
                table: "documents",
                column: "template_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_competence_links");

            migrationBuilder.DropTable(
                name: "document_hours");

            migrationBuilder.DropTable(
                name: "document_status_history");

            migrationBuilder.DropTable(
                name: "document_versions");

            migrationBuilder.DropTable(
                name: "documents");
        }
    }
}

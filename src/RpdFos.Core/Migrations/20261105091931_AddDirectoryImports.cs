using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "directory_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rows_count = table.Column<int>(type: "integer", nullable: false),
                    added_count = table.Column<int>(type: "integer", nullable: false),
                    updated_count = table.Column<int>(type: "integer", nullable: false),
                    replaced_count = table.Column<int>(type: "integer", nullable: false),
                    errors_log = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_directory_imports", x => x.id);
                    table.ForeignKey(
                        name: "fk_directory_imports_employees_started_by",
                        column: x => x.started_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_directory_imports_uploaded_files_file_id",
                        column: x => x.file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_directory_imports_file_id",
                table: "directory_imports",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_directory_imports_started_at",
                table: "directory_imports",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_directory_imports_started_by",
                table: "directory_imports",
                column: "started_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "directory_imports");
        }
    }
}

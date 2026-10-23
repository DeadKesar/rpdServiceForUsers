using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateDefaultPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_payload",
                table: "document_templates",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_payload",
                table: "document_templates");
        }
    }
}

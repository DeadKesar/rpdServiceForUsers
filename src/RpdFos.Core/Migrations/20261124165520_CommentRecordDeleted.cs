using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class CommentRecordDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_record_deleted",
                table: "document_comments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_record_deleted",
                table: "document_comments");
        }
    }
}

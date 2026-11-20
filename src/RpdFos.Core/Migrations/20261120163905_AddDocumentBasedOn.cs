using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentBasedOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "based_on_document_id",
                table: "documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_based_on_document_id",
                table: "documents",
                column: "based_on_document_id");

            migrationBuilder.AddForeignKey(
                name: "fk_documents_documents_based_on_document_id",
                table: "documents",
                column: "based_on_document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_documents_documents_based_on_document_id",
                table: "documents");

            migrationBuilder.DropIndex(
                name: "ix_documents_based_on_document_id",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "based_on_document_id",
                table: "documents");
        }
    }
}

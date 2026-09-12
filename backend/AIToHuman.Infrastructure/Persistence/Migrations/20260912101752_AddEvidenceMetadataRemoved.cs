using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceMetadataRemoved : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MetadataRemoved",
                table: "evidence",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_UploadedBy_CreatedAt",
                table: "evidence",
                columns: new[] { "UploadedBy", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_evidence_UploadedBy_CreatedAt",
                table: "evidence");

            migrationBuilder.DropColumn(
                name: "MetadataRemoved",
                table: "evidence");
        }
    }
}

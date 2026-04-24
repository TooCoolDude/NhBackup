using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NhBackup.WebApplication.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GalleryTag");

            migrationBuilder.DropTable(
                name: "GalleryTags");

            migrationBuilder.DropTable(
                name: "Galleries");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}

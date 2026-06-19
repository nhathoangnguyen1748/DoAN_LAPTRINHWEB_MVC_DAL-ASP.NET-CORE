using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoAnLapTrinhWeb.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DesignerProjects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OwnerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Schema = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DesignerProjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectCollaborators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectCollaborators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectCollaborators_DesignerProjects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "DesignerProjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DesignerProjects_OwnerEmail",
                table: "DesignerProjects",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectCollaborators_Email",
                table: "ProjectCollaborators",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectCollaborators_ProjectId_Email",
                table: "ProjectCollaborators",
                columns: new[] { "ProjectId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectCollaborators");

            migrationBuilder.DropTable(
                name: "DesignerProjects");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCUT_Market.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserStudentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "student_id",
                table: "users",
                type: "varchar(13)",
                maxLength: 13,
                nullable: true,
                collation: "utf8mb4_0900_ai_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "uk_users_student_id",
                table: "users",
                column: "student_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uk_users_student_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "student_id",
                table: "users");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCUT_Market.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationHide : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "buyer_deleted_at",
                table: "conversations",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "seller_deleted_at",
                table: "conversations",
                type: "datetime(3)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buyer_deleted_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "seller_deleted_at",
                table: "conversations");
        }
    }
}

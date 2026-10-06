using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NCUT_Market.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "buyer_confirmed_at",
                table: "products",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "seller_confirmed_at",
                table: "products",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "transaction_accepted_at",
                table: "products",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "transaction_buyer_id",
                table: "products",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "version",
                table: "products",
                type: "int unsigned",
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<DateTime>(
                name: "transaction_proposed_at",
                table: "conversations",
                type: "datetime(3)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "transaction_proposed_by_id",
                table: "conversations",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_products_status_transaction_accepted_at",
                table: "products",
                columns: new[] { "status", "transaction_accepted_at" });

            migrationBuilder.CreateIndex(
                name: "idx_products_transaction_buyer_id",
                table: "products",
                column: "transaction_buyer_id");

            migrationBuilder.CreateIndex(
                name: "idx_conversations_transaction_proposed_at",
                table: "conversations",
                column: "transaction_proposed_at");

            migrationBuilder.CreateIndex(
                name: "IX_conversations_transaction_proposed_by_id",
                table: "conversations",
                column: "transaction_proposed_by_id");

            migrationBuilder.AddForeignKey(
                name: "FK_conversations_users_transaction_proposed_by_id",
                table: "conversations",
                column: "transaction_proposed_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_products_users_transaction_buyer_id",
                table: "products",
                column: "transaction_buyer_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_conversations_users_transaction_proposed_by_id",
                table: "conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_products_users_transaction_buyer_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "idx_products_status_transaction_accepted_at",
                table: "products");

            migrationBuilder.DropIndex(
                name: "idx_products_transaction_buyer_id",
                table: "products");

            migrationBuilder.DropIndex(
                name: "idx_conversations_transaction_proposed_at",
                table: "conversations");

            migrationBuilder.DropIndex(
                name: "IX_conversations_transaction_proposed_by_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "buyer_confirmed_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "seller_confirmed_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "transaction_accepted_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "transaction_buyer_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "version",
                table: "products");

            migrationBuilder.DropColumn(
                name: "transaction_proposed_at",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "transaction_proposed_by_id",
                table: "conversations");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BomComponents",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ComponentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomComponents", x => new { x.TenantId, x.ArticleId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_BomComponents_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomComponents_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomComponents_Articles_TenantId_ComponentId",
                        columns: x => new { x.TenantId, x.ComponentId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomComponents_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProducedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    CompletedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IssueDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceiptDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrders", x => x.Id);
                    table.UniqueConstraint("AK_WorkOrders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_CancelledBy",
                        columns: x => new { x.TenantId, x.CancelledBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_CompletedBy",
                        columns: x => new { x.TenantId, x.CompletedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_ReleasedBy",
                        columns: x => new { x.TenantId, x.ReleasedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_ReversedBy",
                        columns: x => new { x.TenantId, x.ReversedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_StockDocuments_TenantId_IssueDocumentId",
                        columns: x => new { x.TenantId, x.IssueDocumentId },
                        principalTable: "StockDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_StockDocuments_TenantId_ReceiptDocumentId",
                        columns: x => new { x.TenantId, x.ReceiptDocumentId },
                        principalTable: "StockDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrders_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlannedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ConsumedQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOrderLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkOrderLines_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderLines_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOrderLines_WorkOrders_TenantId_WorkOrderId",
                        columns: x => new { x.TenantId, x.WorkOrderId },
                        principalTable: "WorkOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BomComponents_TenantId_ComponentId",
                table: "BomComponents",
                columns: new[] { "TenantId", "ComponentId" });

            migrationBuilder.CreateIndex(
                name: "IX_BomComponents_TenantId_UpdatedBy",
                table: "BomComponents",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLines_TenantId_ArticleId",
                table: "WorkOrderLines",
                columns: new[] { "TenantId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrderLines_TenantId_WorkOrderId_LineNo",
                table: "WorkOrderLines",
                columns: new[] { "TenantId", "WorkOrderId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_ArticleId",
                table: "WorkOrders",
                columns: new[] { "TenantId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_CancelledBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "CancelledBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_CompletedBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "CompletedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_CreatedBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_IssueDocumentId",
                table: "WorkOrders",
                columns: new[] { "TenantId", "IssueDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_Number",
                table: "WorkOrders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_ReceiptDocumentId",
                table: "WorkOrders",
                columns: new[] { "TenantId", "ReceiptDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_ReleasedBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "ReleasedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_ReversedBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "ReversedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_UpdatedBy",
                table: "WorkOrders",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_TenantId_WarehouseId",
                table: "WorkOrders",
                columns: new[] { "TenantId", "WarehouseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BomComponents");

            migrationBuilder.DropTable(
                name: "WorkOrderLines");

            migrationBuilder.DropTable(
                name: "WorkOrders");
        }
    }
}

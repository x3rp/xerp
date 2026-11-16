using Xerp.Application.Common;
using Xerp.Application.Stock;

namespace Xerp.Application.Production;

// ---- bill of materials (spec W01, 4.1)

/// <summary>One component of a request: the article's id as text, so a malformed one is reported with the rest.</summary>
public sealed record BomComponentInput(string? ArticleId = null, decimal? Quantity = null);

/// <summary>The body of <c>PUT /articles/{id}/bom</c>: the whole bill, which replaces the stored one.</summary>
public sealed record SetBomInput(IReadOnlyList<BomComponentInput?>? Components = null);

/// <summary>The arguments of <c>bom_set</c>: the product's id and the bill.</summary>
public sealed record SetBomToolInput(string? ArticleId = null, IReadOnlyList<BomComponentInput?>? Components = null);

/// <summary>The only argument of <c>bom_get</c> and <c>bom_delete</c>.</summary>
public sealed record BomAddressInput(string? ArticleId = null);

/// <summary>A component with its article's current code and name and its base unit; quantity per one base unit of the product.</summary>
public sealed record BomComponentDto(int LineNo, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity);

/// <summary>
/// The bill of materials of a product article. <c>Components</c> is empty when the article has none;
/// <c>UpdatedAt</c> / <c>UpdatedBy</c> are then null.
/// </summary>
public sealed record BomDto(
    ReferenceSummary Article, ReferenceSummary Unit, IReadOnlyList<BomComponentDto> Components, DateTime? UpdatedAt, Guid? UpdatedBy);

public sealed record BomDeleted(bool Deleted = true);

// ---- work orders (spec W01, 4.2)

/// <summary>A component line of a request: the article and the quantity planned, in its base unit.</summary>
public sealed record WorkOrderLineInput(string? ArticleId = null, decimal? PlannedQuantity = null);

/// <summary>
/// The body of a create and of a replace (a replace is a create of the same draft again). <c>Lines</c> omitted
/// or null: the product's bill of materials for <c>Quantity</c>. <c>WarehouseId</c> omitted or null: the
/// tenant's default warehouse.
/// </summary>
public sealed record WorkOrderInput(
    string? ArticleId = null, decimal? Quantity = null, string? PlannedDate = null, string? WarehouseId = null,
    IReadOnlyList<WorkOrderLineInput?>? Lines = null, string? Reference = null, string? Note = null);

/// <summary>The arguments of <c>work_order_update</c>: the id and the body.</summary>
public sealed record ReplaceWorkOrderInput(
    string? ArticleId = null, decimal? Quantity = null, string? PlannedDate = null, string? WarehouseId = null,
    IReadOnlyList<WorkOrderLineInput?>? Lines = null, string? Reference = null, string? Note = null)
{
    public WorkOrderInput ToInput() => new(ArticleId, Quantity, PlannedDate, WarehouseId, Lines, Reference, Note);
}

/// <summary>What was actually consumed of one line, addressed by its <c>lineNo</c>.</summary>
public sealed record WorkOrderConsumptionInput(int? LineNo = null, decimal? ConsumedQuantity = null);

/// <summary>
/// The body of a completion; every field is optional. <c>DocumentDate</c>: the date of both stock documents,
/// default today (UTC). <c>ProducedQuantity</c>: default the order's quantity. <c>Lines</c>: consumption of the
/// lines named; a line not named consumes its planned quantity scaled to what was produced.
/// </summary>
public sealed record CompleteWorkOrderInput(
    string? DocumentDate = null, decimal? ProducedQuantity = null, IReadOnlyList<WorkOrderConsumptionInput?>? Lines = null);

/// <summary>The arguments of <c>work_order_complete</c>.</summary>
public sealed record CompleteWorkOrderToolInput(
    string? Id = null, string? DocumentDate = null, decimal? ProducedQuantity = null, IReadOnlyList<WorkOrderConsumptionInput?>? Lines = null)
{
    public CompleteWorkOrderInput ToInput() => new(DocumentDate, ProducedQuantity, Lines);
}

/// <summary>The body of a reversal: the date of the two reversing documents (default today, UTC) and their note.</summary>
public sealed record ReverseWorkOrderInput(string? DocumentDate = null, string? Note = null);

/// <summary>The arguments of <c>work_order_reverse</c>.</summary>
public sealed record ReverseWorkOrderToolInput(string? Id = null, string? DocumentDate = null, string? Note = null)
{
    public ReverseWorkOrderInput ToInput() => new(DocumentDate, Note);
}

public sealed record ListWorkOrdersInput(
    string? Status = null, string? ArticleId = null, string? WarehouseId = null, string? Search = null, int? Limit = null, int? Offset = null);

/// <summary>How a client without a URL path names one work order: by <c>id</c> or by <c>number</c>.</summary>
public sealed record WorkOrderAddressInput(string? Id = null, string? Number = null);

/// <summary>A component line; <c>ConsumedQuantity</c> is null until the order is completed. Quantities in the component's base unit.</summary>
public sealed record WorkOrderLineDto(int LineNo, ReferenceSummary Article, ReferenceSummary Unit, decimal PlannedQuantity, decimal? ConsumedQuantity);

/// <summary>
/// A work order. <c>Unit</c> is the product's base unit, in which <c>Quantity</c> and <c>ProducedQuantity</c> are
/// given. <c>IssueDocument</c> (the components out) and <c>ReceiptDocument</c> (the product in) are the stock
/// documents its completion posted; null before, and <c>IssueDocument</c> also when nothing was consumed.
/// </summary>
public sealed record WorkOrderDto(
    Guid Id, string Status, string? Number, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity, decimal? ProducedQuantity,
    DateOnly PlannedDate, DateOnly? CompletedDate, ReferenceSummary Warehouse,
    StockDocumentLinkDto? IssueDocument, StockDocumentLinkDto? ReceiptDocument, string? Reference, string? Note,
    IReadOnlyList<WorkOrderLineDto> Lines,
    DateTime CreatedAt, DateTime UpdatedAt, Guid CreatedBy, Guid UpdatedBy,
    DateTime? ReleasedAt, Guid? ReleasedBy, DateTime? CompletedAt, Guid? CompletedBy,
    DateTime? CancelledAt, Guid? CancelledBy, DateTime? ReversedAt, Guid? ReversedBy);

/// <summary>A work order as a list shows it: without its lines, with their count.</summary>
public sealed record WorkOrderSummaryDto(
    Guid Id, string Status, string? Number, ReferenceSummary Article, ReferenceSummary Unit, decimal Quantity, decimal? ProducedQuantity,
    DateOnly PlannedDate, DateOnly? CompletedDate, ReferenceSummary Warehouse, string? Reference, int LineCount,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record WorkOrderDeleted(bool Deleted = true);

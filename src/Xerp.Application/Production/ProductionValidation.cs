using System.Globalization;
using Xerp.Application.Common;
using Xerp.Application.Warehouses;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Production;

namespace Xerp.Application.Production;

/// <summary>Validated bill: the components in the order given.</summary>
public sealed record BomValues(IReadOnlyList<(Guid ArticleId, decimal Quantity)> Components);

/// <summary>Validated header and lines of a work order; <c>Lines</c> null = take them from the bill of materials.</summary>
public sealed record WorkOrderValues(
    Guid ArticleId, decimal Quantity, DateOnly PlannedDate, Guid? WarehouseId, IReadOnlyList<WorkOrderLineEntry>? Lines, string? Reference, string? Note);

/// <summary>Validated completion: the date (null = today), the produced quantity (null = the order's) and the consumption given, by line number.</summary>
public sealed record CompletionValues(DateOnly? DocumentDate, decimal? ProducedQuantity, IReadOnlyList<(int Index, int LineNo, decimal Consumed)> Lines);

public sealed record WorkOrderReversalValues(DateOnly? DocumentDate, string? Note);

public sealed record WorkOrderListQuery(WorkOrderStatus? Status, Guid? ArticleId, Guid? WarehouseId, string? Search, int Limit, int Offset);

/// <summary>
/// Input rules of spec W01 (section 4). No I/O. Every invalid field is reported together; errors about a line
/// are keyed by its position in the request: <c>components[0].quantity</c>, <c>lines[0].plannedQuantity</c>.
/// </summary>
public static class ProductionValidation
{
    private const string DateFormat = "yyyy-MM-dd";

    public static string Key(string list, int index, string field) => $"{list}[{index}].{field}";

    private static readonly string QuantityMessage =
        $"must be greater than 0, at most {QuantityRules.Max.ToString(CultureInfo.InvariantCulture)}, with at most {QuantityRules.DecimalPlaces} decimal places.";

    public static Result<BomValues> Bom(IReadOnlyList<BomComponentInput?>? components)
    {
        var errors = new ValidationErrors();
        var result = new List<(Guid, decimal)>();
        if (components is null || components.Count is < 1 or > ProductionRules.MaxComponents)
            errors.Add("components", $"components is required: 1 to {ProductionRules.MaxComponents} components. To remove the bill, delete it.");
        else
        {
            for (var i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c is null)
                {
                    errors.Add($"components[{i}]", "A component must be an object with articleId and quantity.");
                    continue;
                }
                var articleId = Uuid(errors, Key("components", i, "articleId"), c.ArticleId, "an article");
                var quantity = Quantity(errors, Key("components", i, "quantity"), c.Quantity);
                result.Add((articleId, quantity));
            }
        }
        return errors.Any ? errors.ToError() : new BomValues(result);
    }

    public static Result<WorkOrderValues> WorkOrder(WorkOrderInput input)
    {
        var errors = new ValidationErrors();
        var articleId = Uuid(errors, "articleId", input.ArticleId, "an article");
        var quantity = Quantity(errors, "quantity", input.Quantity);
        var plannedDate = Date(errors, "plannedDate", input.PlannedDate) ?? default;
        if (input.PlannedDate is null)
            errors.Add("plannedDate", "plannedDate is required, as YYYY-MM-DD.");
        var warehouseId = DefaultWarehouseRules.Named(errors, input.WarehouseId, optional: true);

        List<WorkOrderLineEntry>? lines = null;
        if (input.Lines is { } given)
        {
            lines = [];
            if (given.Count is < 1 or > ProductionRules.MaxComponents)
                errors.Add("lines", $"lines must have 1 to {ProductionRules.MaxComponents} components; leave lines out to take them from the article's bill of materials.");
            for (var i = 0; i < given.Count; i++)
            {
                var l = given[i];
                if (l is null)
                {
                    errors.Add($"lines[{i}]", "A line must be an object with articleId and plannedQuantity.");
                    continue;
                }
                var component = Uuid(errors, Key("lines", i, "articleId"), l.ArticleId, "an article");
                var planned = Quantity(errors, Key("lines", i, "plannedQuantity"), l.PlannedQuantity);
                lines.Add(new WorkOrderLineEntry(component, planned));
            }
        }

        if (!OptionalTextRules.TryNormalize(input.Reference, StockDocument.ReferenceMaxLength, out var reference))
            errors.Add("reference", $"reference must be at most {StockDocument.ReferenceMaxLength} characters on a single line, without control characters.");
        var note = Note(errors, input.Note);
        return errors.Any ? errors.ToError() : new WorkOrderValues(articleId, quantity, plannedDate, warehouseId, lines, reference, note);
    }

    public static Result<CompletionValues> Complete(CompleteWorkOrderInput input)
    {
        var errors = new ValidationErrors();
        var date = Date(errors, "documentDate", input.DocumentDate);
        decimal? produced = null;
        if (input.ProducedQuantity is not null)
            produced = Quantity(errors, "producedQuantity", input.ProducedQuantity);
        var lines = new List<(int, int, decimal)>();
        var seen = new HashSet<int>();
        for (var i = 0; i < (input.Lines?.Count ?? 0); i++)
        {
            var l = input.Lines![i];
            if (l is null)
            {
                errors.Add($"lines[{i}]", "A line must be an object with lineNo and consumedQuantity.");
                continue;
            }
            if (l.LineNo is not >= 1)
                errors.Add(Key("lines", i, "lineNo"), "lineNo is required: the number (1…n) of the work order line.");
            else if (!seen.Add(l.LineNo.Value))
                errors.Add(Key("lines", i, "lineNo"), "Each line may be named once.");
            if (l.ConsumedQuantity is not { } consumed || !ProductionRules.IsValidConsumption(consumed))
                errors.Add(Key("lines", i, "consumedQuantity"),
                    $"consumedQuantity is required: 0 (none was used) or a quantity greater than 0, at most {QuantityRules.Max.ToString(CultureInfo.InvariantCulture)}, with at most {QuantityRules.DecimalPlaces} decimal places.");
            lines.Add((i, l.LineNo ?? 0, l.ConsumedQuantity ?? 0));
        }
        return errors.Any ? errors.ToError() : new CompletionValues(date, produced, lines);
    }

    public static Result<WorkOrderReversalValues> Reverse(ReverseWorkOrderInput input)
    {
        var errors = new ValidationErrors();
        var date = Date(errors, "documentDate", input.DocumentDate);
        var note = Note(errors, input.Note);
        return errors.Any ? errors.ToError() : new WorkOrderReversalValues(date, note);
    }

    public static Result<WorkOrderListQuery> List(ListWorkOrdersInput input)
    {
        var errors = new ValidationErrors();
        WorkOrderStatus? status = null;
        if (input.Status is not null)
        {
            if (WorkOrderStatusNames.TryParse(input.Status, out var parsed)) status = parsed;
            else errors.Add("status", $"status must be one of: {string.Join(", ", WorkOrderStatusNames.All)}.");
        }
        Guid? articleId = input.ArticleId is null ? null : Uuid(errors, "articleId", input.ArticleId, "an article");
        Guid? warehouseId = input.WarehouseId is null ? null : Uuid(errors, "warehouseId", input.WarehouseId, "a warehouse");
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        return errors.Any ? errors.ToError() : new WorkOrderListQuery(status, articleId, warehouseId, search, limit, offset);
    }

    private static Guid Uuid(ValidationErrors errors, string field, string? input, string what)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, string.IsNullOrEmpty(input) ? $"{field} is required." : $"{field} must be the id (UUID) of {what}, not its code.");
        return default;
    }

    private static decimal Quantity(ValidationErrors errors, string field, decimal? input)
    {
        if (input is { } value && QuantityRules.IsValid(value))
            return value;
        errors.Add(field, input is null ? $"{field} is required: a number that {QuantityMessage}" : $"{field} {QuantityMessage}");
        return 0;
    }

    /// <summary>Null when absent (the caller decides whether that is allowed) or invalid (then an error was added).</summary>
    private static DateOnly? Date(ValidationErrors errors, string field, string? input)
    {
        if (input is null)
            return null;
        if (DateOnly.TryParseExact(input, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        errors.Add(field, $"{field} must be a calendar date written as YYYY-MM-DD, for example 2026-11-16.");
        return null;
    }

    private static string? Note(ValidationErrors errors, string? note)
    {
        if (!NoteRules.TryNormalize(note, StockDocument.NoteMaxLength, out var normalized))
            errors.Add("note", $"note must be at most {StockDocument.NoteMaxLength} characters and contain no control characters other than line breaks and tabs.");
        return normalized;
    }
}

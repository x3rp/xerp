using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.Stock;
using Xerp.Application.Warehouses;
using Xerp.Domain.Inventory;
using Xerp.Domain.Production;
using Xerp.Domain.Rules;

namespace Xerp.Application.Production;

/// <summary>
/// Work orders (spec W01, 4.2). One method = one HTTP endpoint = one MCP tool. Every write runs inside
/// <see cref="IXerpDb.SerializedPerTenantAsync{T}"/>, the lock stock postings take too: a completion posts its
/// issue of components and its receipt of the product with <see cref="StockDocumentOperations"/> in the same
/// transaction, judged by the same stock rules, and saves once - everything or nothing.
/// </summary>
public sealed class WorkOrderOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules, StockDocumentOperations stock)
{
    private const string NotFoundDetail = "Work order not found.";

    public async Task<Result<PagedResult<WorkOrderSummaryDto>>> ListAsync(ListWorkOrdersInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.List(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var q = validated.Value;

        var orders = db.WorkOrders.AsNoTracking();
        if (q.Status is { } status)
            orders = orders.Where(o => o.Status == status);
        if (q.ArticleId is { } articleId)
            orders = orders.Where(o => o.ArticleId == articleId);
        if (q.WarehouseId is { } warehouseId)
            orders = orders.Where(o => o.WarehouseId == warehouseId);
        if (q.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            orders = orders.Where(o =>
                (o.Number != null && EF.Functions.Like(DbText.Lower(o.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter)) ||
                (o.Reference != null && EF.Functions.Like(DbText.Lower(o.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }
        var total = await orders.CountAsync(cancellationToken);
        var page = await orders.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip(q.Offset).Take(q.Limit).ToListAsync(cancellationToken);
        var ids = page.Select(o => o.Id).ToList();
        var lineCounts = await db.WorkOrderLines.Where(l => ids.Contains(l.WorkOrderId))
            .GroupBy(l => l.WorkOrderId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var related = await RelatedAsync(page, [], cancellationToken);
        var items = page.Select(o => new WorkOrderSummaryDto(
            o.Id, o.Status.ToName(), o.Number, related.Article(o.ArticleId), related.UnitOf(o.ArticleId), QuantityRules.Normalize(o.Quantity),
            Normalize(o.ProducedQuantity), o.PlannedDate, o.CompletedDate, related.Warehouses[o.WarehouseId], o.Reference,
            lineCounts.GetValueOrDefault(o.Id), o.CreatedAt, o.UpdatedAt)).ToList();
        return new PagedResult<WorkOrderSummaryDto>(items, total, q.Limit, q.Offset);
    }

    public async Task<Result<WorkOrderDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var order = await db.WorkOrders.AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        return order is null ? NotFound() : await ToDtoAsync(order, cancellationToken);
    }

    public async Task<Result<WorkOrderDto>> FindAsync(WorkOrderAddressInput address, CancellationToken cancellationToken = default)
    {
        if (address.Id is null == address.Number is null)
        {
            const string message = "Give exactly one of id and number.";
            return AppError.Validation(new Dictionary<string, string[]> { ["id"] = [message], ["number"] = [message] });
        }
        if (address.Number is { } number)
        {
            if (string.IsNullOrWhiteSpace(number) || number.Length > WorkOrder.NumberMaxLength)
                return NotFound();
            var order = await db.WorkOrders.AsNoTracking().Include(o => o.Lines)
                .SingleOrDefaultAsync(o => o.Number != null && DbText.Lower(o.Number) == DbText.Lower(number), cancellationToken);
            return order is null ? NotFound() : await ToDtoAsync(order, cancellationToken);
        }
        return Guid.TryParse(address.Id, out var id) ? await GetAsync(id, cancellationToken) : NotFound();
    }

    /// <summary>
    /// Creates a draft. Order of checks: form -> product (known, newly assigned active, stock) -> warehouse (omitted:
    /// the default) -> lines (omitted: from the bill; given: rule <c>production.componentsMayDifferFromBom</c>) ->
    /// components (known, newly assigned active, stock, not the product).
    /// </summary>
    public async Task<Result<WorkOrderDto>> CreateAsync(WorkOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.WorkOrder(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var checkedValues = await CheckAsync(values, stored: null, tenantRules, ct);
            if (!checkedValues.IsSuccess)
                return checkedValues.Error;
            var (warehouseId, lines) = checkedValues.Value;
            var order = WorkOrder.Create(values.ArticleId, values.Quantity, warehouseId, values.PlannedDate, values.Reference, values.Note,
                lines, clock.UtcNow, ActorKeyId());
            db.WorkOrders.Add(order);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);
    }

    /// <summary>Replaces a draft as a whole, checked like a create; a reference the draft already has may stay although inactive.</summary>
    public async Task<Result<WorkOrderDto>> ReplaceAsync(Guid id, WorkOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.WorkOrder(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.IsDraft)
                return WrongStatus(order, "changed");
            var checkedValues = await CheckAsync(values, order, tenantRules, ct);
            if (!checkedValues.IsSuccess)
                return checkedValues.Error;
            var (warehouseId, lines) = checkedValues.Value;
            var removed = order.Replace(values.ArticleId, values.Quantity, warehouseId, values.PlannedDate, values.Reference, values.Note,
                lines, clock.UtcNow, ActorKeyId());
            db.WorkOrderLines.RemoveRange(removed);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);
    }

    public Task<Result<WorkOrderDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<WorkOrderDeleted>>(async ct =>
        {
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.IsDraft)
                return WrongStatus(order, "deleted");
            db.WorkOrderLines.RemoveRange(order.Lines);
            db.WorkOrders.Remove(order);
            await db.SaveChangesAsync(ct);
            return new WorkOrderDeleted();
        }, cancellationToken);

    /// <summary>Gives a draft its number (<c>WO-…</c>); header and lines are fixed from then on. Inactive masters are refused.</summary>
    public Task<Result<WorkOrderDto>> ReleaseAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.IsDraft)
                return WrongStatus(order, "released");
            if (await ActiveForProductionAsync(order, ct) is { } inactive)
                return inactive;
            order.Release(await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.WorkOrder, ct), clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);

    public Task<Result<WorkOrderDto>> CancelAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (order.Status != WorkOrderStatus.Released)
                return WrongStatus(order, "cancelled");
            order.Cancel(clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);

    /// <summary>
    /// Books production: an issue of what was consumed and a receipt of what was produced, both posted, dated
    /// <c>documentDate</c>, in the order's warehouse, with the order's number as their reference. A draft is
    /// released on the way. Order of checks: form -> exists -> status -> masters active -> line numbers ->
    /// rules <c>production.producedQuantityMayDifferFromPlan</c> and <c>production.consumptionMayDifferFromPlan</c>
    /// -> the issue as any posting (<c>stock.negativeStockAllowed</c>, <c>sales.reservedStockProtected</c>) -> the receipt.
    /// Stock errors of the issue are keyed <c>lines[lineNo - 1].consumedQuantity</c>.
    /// </summary>
    public async Task<Result<WorkOrderDto>> CompleteAsync(Guid id, CompleteWorkOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.Complete(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (!order.CanBeCompleted)
                return WrongStatus(order, "completed");
            if (await ActiveForProductionAsync(order, ct) is { } inactive)
                return inactive;

            var lines = order.Lines;
            var errors = new ValidationErrors();
            foreach (var (index, lineNo, _) in values.Lines)
            {
                if (lineNo > lines.Count)
                    errors.Add(ProductionValidation.Key("lines", index, "lineNo"), $"The work order has no line {lineNo}; its lines are 1 to {lines.Count}.");
            }
            if (errors.Any)
                return errors.ToError();

            var produced = values.ProducedQuantity ?? order.Quantity;
            var producedAllowed = tenantRules[RuleRegistry.ProductionProducedQuantityMayDifferFromPlan];
            if (produced != order.Quantity && !producedAllowed)
                errors.Add("producedQuantity",
                    $"producedQuantity must be the work order's quantity, {Text(order.Quantity)}: this company's rule {RuleRegistry.ProductionProducedQuantityMayDifferFromPlan.Key} is false.",
                    RuleRegistry.ProductionProducedQuantityMayDifferFromPlan, producedAllowed);
            var consumed = lines.Select(l => ProductionRules.Scaled(l.PlannedQuantity, order.Quantity, produced)).ToArray();
            var consumptionAllowed = tenantRules[RuleRegistry.ProductionConsumptionMayDifferFromPlan];
            foreach (var (index, lineNo, quantity) in values.Lines)
            {
                if (quantity != consumed[lineNo - 1] && !consumptionAllowed)
                    errors.Add(ProductionValidation.Key("lines", index, "consumedQuantity"),
                        $"consumedQuantity must be {Text(consumed[lineNo - 1])}, what the line plans for the produced quantity: this company's rule {RuleRegistry.ProductionConsumptionMayDifferFromPlan.Key} is false.",
                        RuleRegistry.ProductionConsumptionMayDifferFromPlan, consumptionAllowed);
                consumed[lineNo - 1] = quantity;
            }
            if (errors.Any)
                return errors.ToError();

            var date = values.DocumentDate ?? DateOnly.FromDateTime(clock.UtcNow);
            var now = clock.UtcNow;
            var actor = ActorKeyId();
            var articles = await ProductionReads.ArticlesAsync(db, lines.Select(l => l.ArticleId).Append(order.ArticleId), ct);
            // The number is needed as the documents' reference; a refusal below saves nothing, so no number is lost.
            if (order.IsDraft)
                order.Release(await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.WorkOrder, ct), now, actor);
            var number = order.Number!;

            // The issue: one line per consumed component, in line order.
            var consuming = Enumerable.Range(0, lines.Count).Where(i => consumed[i] > 0).ToList();
            StockDocument? issue = null;
            if (consuming.Count > 0)
            {
                issue = StockDocument.Create(StockDocumentType.Issue, date, order.WarehouseId, null, number, $"Components of work order {number}.",
                    consuming.Select(i => new StockLineEntry(lines[i].ArticleId, articles[lines[i].ArticleId].BaseUnitId, consumed[i])).ToList(), now, actor);
                db.StockDocuments.Add(issue);
                if (await stock.PostWithinLockAsync(issue, tenantRules, ct) is { } refused)
                    return Rekey(refused, consuming);
            }
            var receipt = StockDocument.Create(StockDocumentType.Receipt, date, order.WarehouseId, null, number, $"Product of work order {number}.",
                [new StockLineEntry(order.ArticleId, articles[order.ArticleId].BaseUnitId, produced)], now, actor);
            db.StockDocuments.Add(receipt);
            if (await stock.PostWithinLockAsync(receipt, tenantRules, ct) is { } receiptRefused)
                return RekeyProduct(receiptRefused);

            order.Complete(produced, consumed, date, issue?.Id, receipt.Id, now, actor);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);
    }

    /// <summary>
    /// Undoes a completed order: its receipt is reversed (the product leaves stock again), then its issue (the
    /// components come back), both judged by the stock rules like any reversal; the order becomes <c>reversed</c>.
    /// </summary>
    public async Task<Result<WorkOrderDto>> ReverseAsync(Guid id, ReverseWorkOrderInput input, CancellationToken cancellationToken = default)
    {
        var validated = ProductionValidation.Reverse(input);
        if (!validated.IsSuccess)
            return validated.Error;
        var values = validated.Value;

        return await db.SerializedPerTenantAsync<Result<WorkOrderDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var order = await db.WorkOrders.Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
                return NotFound();
            if (order.Status != WorkOrderStatus.Completed)
                return WrongStatus(order, "reversed");
            var reversal = new StockReversalValues(values.DocumentDate ?? DateOnly.FromDateTime(clock.UtcNow), values.Note);

            var receipt = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == order.ReceiptDocumentId, ct);
            var reversedReceipt = await stock.ReverseWithinLockAsync(receipt, reversal, tenantRules, ct);
            if (!reversedReceipt.IsSuccess)
                return RekeyProduct(reversedReceipt.Error!);
            if (order.IssueDocumentId is { } issueId)
            {
                var issue = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == issueId, ct);
                var consuming = order.Lines.Select((l, i) => (l, i)).Where(x => x.l.ConsumedQuantity > 0).Select(x => x.i).ToList();
                var reversedIssue = await stock.ReverseWithinLockAsync(issue, reversal, tenantRules, ct);
                if (!reversedIssue.IsSuccess)
                    return Rekey(reversedIssue.Error!, consuming);
            }
            order.MarkReversed(clock.UtcNow, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(order, ct);
        }, cancellationToken);
    }

    // ---- the string-id overloads, for MCP tools (spec 003, R15: an id that is not a UUID names no record)

    public Task<Result<WorkOrderDto>> ReplaceAsync(string? id, ReplaceWorkOrderInput input, CancellationToken cancellationToken = default) =>
        ById(id, parsed => ReplaceAsync(parsed, input.ToInput(), cancellationToken));

    public Task<Result<WorkOrderDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        ById(id, parsed => DeleteAsync(parsed, cancellationToken));

    public Task<Result<WorkOrderDto>> ReleaseAsync(string? id, CancellationToken cancellationToken = default) =>
        ById(id, parsed => ReleaseAsync(parsed, cancellationToken));

    public Task<Result<WorkOrderDto>> CancelAsync(string? id, CancellationToken cancellationToken = default) =>
        ById(id, parsed => CancelAsync(parsed, cancellationToken));

    public Task<Result<WorkOrderDto>> CompleteAsync(CompleteWorkOrderToolInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, parsed => CompleteAsync(parsed, input.ToInput(), cancellationToken));

    public Task<Result<WorkOrderDto>> ReverseAsync(ReverseWorkOrderToolInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, parsed => ReverseAsync(parsed, input.ToInput(), cancellationToken));

    private static Task<Result<T>> ById<T>(string? id, Func<Guid, Task<Result<T>>> operation) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error ? Task.FromResult<Result<T>>(error) : operation(parsed);

    // ---- checks

    private sealed record CheckedWorkOrder(Guid WarehouseId, IReadOnlyList<WorkOrderLineEntry> Lines);

    private async Task<Result<CheckedWorkOrder>> CheckAsync(WorkOrderValues values, WorkOrder? stored, TenantRules tenantRules, CancellationToken ct)
    {
        var bom = await db.BomComponents.AsNoTracking().Where(c => c.ArticleId == values.ArticleId).OrderBy(c => c.LineNo).ToListAsync(ct);
        var componentIds = (values.Lines?.Select(l => l.ArticleId) ?? bom.Select(c => c.ComponentId)).ToList();
        var articles = await ProductionReads.ArticlesAsync(db, componentIds.Append(values.ArticleId), ct);

        if (ProductionReads.Check([new ArticleReference("articleId", values.ArticleId, stored?.ArticleId == values.ArticleId)], articles) is { } productError)
            return productError;

        var warehouseId = values.WarehouseId ?? await WarehouseReads.DefaultIdAsync(db, ct);
        if (await ReferenceCheck.ValidateAsync(db.Warehouses.Where(w => w.Id == warehouseId).Select(w => w.IsActive),
                DefaultWarehouseRules.WarehouseField, warehouseId, stored?.WarehouseId == warehouseId, ct) is { } warehouseError)
            return warehouseError;

        var fromBom = bom.Select(c => new WorkOrderLineEntry(c.ComponentId, ProductionRules.Planned(c.Quantity, values.Quantity))).ToList();
        IReadOnlyList<WorkOrderLineEntry> lines;
        if (values.Lines is null)
        {
            if (fromBom.Count == 0)
                return AppError.Validation("lines",
                    "The article has no bill of materials: set one with bom_set (PUT /articles/{id}/bom), or give the lines of the work order.");
            if (fromBom.Select((l, i) => (l, i)).Where(x => x.l.PlannedQuantity == 0).ToList() is { Count: > 0 } zero)
                return AppError.Validation("quantity",
                    $"quantity is too small for the bill of materials: {zero.Count} component(s) would be planned at 0 after rounding to {QuantityRules.DecimalPlaces} decimals. Make more, or give the lines.");
            lines = fromBom;
        }
        else
        {
            var allowed = tenantRules[RuleRegistry.ProductionComponentsMayDifferFromBom];
            if (!allowed && !values.Lines.SequenceEqual(fromBom))
            {
                var errors = new ValidationErrors();
                errors.Add("lines",
                    $"The lines differ from what the article's bill of materials gives for quantity {Text(values.Quantity)}: this company's rule {RuleRegistry.ProductionComponentsMayDifferFromBom.Key} is false. Leave lines out to take them from the bill.",
                    RuleRegistry.ProductionComponentsMayDifferFromBom, allowed);
                return errors.ToError();
            }
            lines = values.Lines;
        }

        var storedComponents = stored?.Lines.Select(l => l.ArticleId).ToHashSet() ?? [];
        var references = lines.Select((l, i) => new ArticleReference(ProductionValidation.Key("lines", i, "articleId"), l.ArticleId, storedComponents.Contains(l.ArticleId))).ToList();
        if (ProductionReads.Check(references, articles) is { } componentError)
            return componentError;
        var self = lines.Select((l, i) => (l, i)).Where(x => x.l.ArticleId == values.ArticleId).ToList();
        if (self.Count > 0)
            return AppError.Validation(self.ToDictionary(
                x => ProductionValidation.Key("lines", x.i, "articleId"), _ => new[] { "The product cannot be its own component." }));
        return new CheckedWorkOrder(warehouseId, lines);
    }

    /// <summary>At release and completion: the product, the warehouse and every component must be active (REFERENCE_INACTIVE).</summary>
    private async Task<AppError?> ActiveForProductionAsync(WorkOrder order, CancellationToken ct)
    {
        if (!await db.Warehouses.Where(w => w.Id == order.WarehouseId).Select(w => w.IsActive).SingleAsync(ct))
            return new AppError(ErrorCodes.ReferenceInactive, "The work order's warehouse is inactive: reactivate it, or change the draft.",
                new Dictionary<string, string[]> { [DefaultWarehouseRules.WarehouseField] = [$"The record with id '{order.WarehouseId}' is inactive."] });
        var lines = order.Lines;
        var articles = await ProductionReads.ArticlesAsync(db, lines.Select(l => l.ArticleId).Append(order.ArticleId), ct);
        return ProductionReads.Check(
            [new ArticleReference("articleId", order.ArticleId, true),
                .. lines.Select((l, i) => new ArticleReference(ProductionValidation.Key("lines", i, "articleId"), l.ArticleId, true))],
            articles, inactiveAlwaysRefused: true);
    }

    /// <summary>
    /// An error of the issue document, keyed by the work order's lines: line k of the issue is the work order's
    /// line <paramref name="lineOfIssueLine"/>[k], and its quantity is the consumed quantity.
    /// </summary>
    private static AppError Rekey(AppError error, IReadOnlyList<int> lineOfIssueLine) =>
        error.Errors is null ? error : error with
        {
            Errors = error.Errors.ToDictionary(
                e => RekeyField(e.Key, k => k < lineOfIssueLine.Count ? $"lines[{lineOfIssueLine[k]}]" : $"lines[{k}]", "consumedQuantity"),
                e => e.Value, StringComparer.Ordinal),
        };

    /// <summary>An error of the receipt: its one line is the product.</summary>
    private static AppError RekeyProduct(AppError error) =>
        error.Errors is null ? error : error with
        {
            Errors = error.Errors.ToDictionary(
                e => e.Key.StartsWith("lines[", StringComparison.Ordinal)
                    ? e.Key.EndsWith(".articleId", StringComparison.Ordinal) ? "articleId" : "producedQuantity"
                    : e.Key,
                e => e.Value, StringComparer.Ordinal),
        };

    private static string RekeyField(string key, Func<int, string> line, string quantityField)
    {
        if (!key.StartsWith("lines[", StringComparison.Ordinal))
            return key;
        var close = key.IndexOf(']');
        if (close < 0 || !int.TryParse(key.AsSpan(6, close - 6), out var k))
            return key;
        var rest = key[(close + 1)..];
        return line(k) + (rest == ".quantity" ? "." + quantityField : rest);
    }

    // ---- representation

    private sealed record Related(
        IReadOnlyDictionary<Guid, ProductionArticle> Articles, IReadOnlyDictionary<Guid, ReferenceSummary> Units,
        IReadOnlyDictionary<Guid, ReferenceSummary> Warehouses, IReadOnlyDictionary<Guid, StockDocumentLinkDto> Documents)
    {
        public ReferenceSummary Article(Guid id) => new(id, Articles[id].Code, Articles[id].Name);
        public ReferenceSummary UnitOf(Guid articleId) => Units[Articles[articleId].BaseUnitId];
        public StockDocumentLinkDto? Document(Guid? id) => id is { } value ? Documents[value] : null;
    }

    private async Task<Related> RelatedAsync(IReadOnlyCollection<WorkOrder> orders, IEnumerable<Guid> componentIds, CancellationToken ct)
    {
        var articles = await ProductionReads.ArticlesAsync(db, orders.Select(o => o.ArticleId).Concat(componentIds), ct);
        var units = await ProductionReads.UnitsAsync(db, articles.Values.Select(a => a.BaseUnitId), ct);
        var warehouseIds = orders.Select(o => o.WarehouseId).Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking().Where(w => warehouseIds.Contains(w.Id))
            .Select(w => new ReferenceSummary(w.Id, w.Code, w.Name)).ToDictionaryAsync(w => w.Id, ct);
        var documentIds = orders.SelectMany(o => new[] { o.IssueDocumentId, o.ReceiptDocumentId }).Where(d => d is not null).Select(d => d!.Value).ToList();
        var documents = documentIds.Count == 0
            ? []
            : await db.StockDocuments.AsNoTracking().Where(d => documentIds.Contains(d.Id))
                .Select(d => new StockDocumentLinkDto(d.Id, d.Number!)).ToDictionaryAsync(d => d.Id, ct);
        return new Related(articles, units, warehouses, documents);
    }

    private async Task<WorkOrderDto> ToDtoAsync(WorkOrder o, CancellationToken ct)
    {
        var related = await RelatedAsync([o], o.Lines.Select(l => l.ArticleId), ct);
        var lines = o.Lines.Select(l => new WorkOrderLineDto(
            l.LineNo, related.Article(l.ArticleId), related.UnitOf(l.ArticleId), QuantityRules.Normalize(l.PlannedQuantity), Normalize(l.ConsumedQuantity))).ToList();
        return new WorkOrderDto(
            o.Id, o.Status.ToName(), o.Number, related.Article(o.ArticleId), related.UnitOf(o.ArticleId), QuantityRules.Normalize(o.Quantity),
            Normalize(o.ProducedQuantity), o.PlannedDate, o.CompletedDate, related.Warehouses[o.WarehouseId],
            related.Document(o.IssueDocumentId), related.Document(o.ReceiptDocumentId), o.Reference, o.Note, lines,
            o.CreatedAt, o.UpdatedAt, o.CreatedBy, o.UpdatedBy, o.ReleasedAt, o.ReleasedBy, o.CompletedAt, o.CompletedBy,
            o.CancelledAt, o.CancelledBy, o.ReversedAt, o.ReversedBy);
    }

    private static decimal? Normalize(decimal? quantity) => quantity is { } q ? QuantityRules.Normalize(q) : null;

    private static string Text(decimal quantity) => QuantityRules.Normalize(quantity).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);

    private static AppError WrongStatus(WorkOrder order, string attempted) =>
        AppError.InvalidState($"The work order is {order.Status.ToName()} and cannot be {attempted}. " + order.Status switch
        {
            WorkOrderStatus.Draft => "Release it (work_order_release) or complete it (work_order_complete); delete a draft instead of cancelling it.",
            WorkOrderStatus.Released => "A released order can be completed or cancelled.",
            WorkOrderStatus.Completed => "A completed order can only be reversed (work_order_reverse).",
            _ => "It is final; create a new work order.",
        });

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("A work order can only be written by a tenant API key.");

    private Guid TenantId() =>
        context.TenantId ?? throw new InvalidOperationException("A work order belongs to a tenant.");
}

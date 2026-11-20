using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Application.Sales;
using Xerp.Application.Stock;
using Xerp.Domain.Catalog;
using Xerp.Domain.Common;
using Xerp.Domain.Inventory;
using Xerp.Domain.Invoicing;
using Xerp.Domain.Orders;
using Xerp.Domain.Rules;

namespace Xerp.Application.Invoicing;

/// <summary>
/// Outgoing invoices (spec I01): drafts are created, replaced and deleted freely; issuing numbers them from the
/// gapless series <c>RN</c> and, with <c>invoice.issueMovesStock</c>, posts a stock issue in the same transaction;
/// cancelling issues a cancelling invoice and reverses that issue. Payments are recorded against issued invoices.
/// Every write runs under the tenant's lock, the lock stock postings and numbering take too.
/// </summary>
public sealed class InvoiceOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules, StockDocumentOperations stock)
{
    private const string NotFoundDetail = "Invoice not found.";
    private const string DateFormat = "yyyy-MM-dd";

    private sealed record LineValues(int Index, Guid ArticleId, decimal Quantity, decimal? UnitPrice, decimal Discount, Guid? TaxRateId, string? Description);

    private sealed record Values(
        Guid? CustomerId, DateOnly IssueDate, DateOnly? DeliveryDate, DateOnly? DueDate, Guid? PaymentMethodId, Guid? WarehouseId,
        string? Reference, string? Note, IReadOnlyList<LineValues> Lines);

    // ---- reads

    public async Task<Result<PagedResult<InvoiceSummaryDto>>> ListAsync(ListInvoicesInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        InvoiceStatus? status = null;
        if (input.Status is not null)
        {
            if (InvoiceStatusNames.TryParse(input.Status, out var parsed)) status = parsed;
            else errors.Add("status", $"status must be one of: {string.Join(", ", InvoiceStatusNames.All)}.");
        }
        if (input.PaymentStatus is not null && !PaymentStatusNames.Filterable.Contains(input.PaymentStatus))
            errors.Add("paymentStatus", $"paymentStatus must be one of: {string.Join(", ", PaymentStatusNames.Filterable)}.");
        var customerId = OptionalUuid(errors, "customerId", input.CustomerId);
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();

        var invoices = db.Invoices.AsNoTracking();
        if (status is { } s)
            invoices = invoices.Where(i => i.Status == s);
        if (customerId is { } c)
            invoices = invoices.Where(i => i.CustomerId == c);
        // Only issued invoices that are no cancellation are paid against (the rule of PaymentStatus).
        invoices = input.PaymentStatus switch
        {
            PaymentStatusNames.Paid => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.CancellationOfId == null && i.TotalAmount > 0 && i.PaidAmount >= i.TotalAmount),
            PaymentStatusNames.Partial => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.CancellationOfId == null && i.PaidAmount > 0 && i.PaidAmount < i.TotalAmount),
            PaymentStatusNames.Unpaid => invoices.Where(i => i.Status == InvoiceStatus.Issued && i.CancellationOfId == null && i.TotalAmount > 0 && i.PaidAmount == 0),
            _ => invoices,
        };
        if (search is not null)
        {
            var pattern = LikePattern.Contains(search);
            invoices = invoices.Where(i =>
                (i.Number != null && EF.Functions.Like(DbText.Lower(i.Number), DbText.Lower(pattern), LikePattern.EscapeCharacter))
                || (i.Reference != null && EF.Functions.Like(DbText.Lower(i.Reference), DbText.Lower(pattern), LikePattern.EscapeCharacter)));
        }
        var total = await invoices.CountAsync(cancellationToken);
        var rows = await (
                from i in invoices
                join p in db.Partners on i.CustomerId equals p.Id into customers
                from p in customers.DefaultIfEmpty()
                orderby i.CreatedAt descending, i.Id descending
                select new { Invoice = i, Customer = p == null ? null : new ReferenceSummary(p.Id, p.Code, p.Name), LineCount = db.InvoiceLines.Count(l => l.InvoiceId == i.Id) })
            .Skip(offset).Take(limit).ToListAsync(cancellationToken);
        var items = rows.Select(r => new InvoiceSummaryDto(
            r.Invoice.Id, r.Invoice.Status.ToName(), r.Invoice.Number, r.Customer, r.Invoice.IssueDate, r.Invoice.DueDate,
            r.Invoice.NetAmount, r.Invoice.TaxAmount, r.Invoice.TotalAmount, r.Invoice.PaidAmount, Open(r.Invoice), PaymentStatusOf(r.Invoice),
            r.Invoice.CancellationOfId is not null, r.LineCount, r.Invoice.CreatedAt, r.Invoice.UpdatedAt)).ToList();
        return new PagedResult<InvoiceSummaryDto>(items, total, limit, offset);
    }

    public async Task<Result<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var invoice = await Load(db.Invoices.AsNoTracking()).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return invoice is null ? NotFound() : await ToDtoAsync(invoice, cancellationToken);
    }

    public async Task<Result<InvoiceDto>> FindAsync(InvoiceAddressInput input, CancellationToken cancellationToken = default)
    {
        if ((input.Id is null) == (input.Number is null))
            return AppError.Validation("id", "Give exactly one of id and number.");
        if (input.Id is not null)
            return RecordAddress.Id(input.Id, NotFoundDetail, out var id) is { } error ? error : await GetAsync(id, cancellationToken);
        var number = input.Number!.Trim().ToUpperInvariant();
        var found = await db.Invoices.AsNoTracking().Where(i => i.Number == number).Select(i => (Guid?)i.Id).SingleOrDefaultAsync(cancellationToken);
        return found is { } byNumber ? await GetAsync(byNumber, cancellationToken) : NotFound();
    }

    // ---- drafts

    public async Task<Result<InvoiceDto>> CreateAsync(InvoiceInput input, CancellationToken cancellationToken = default)
    {
        var validated = Validate(input);
        if (!validated.IsSuccess)
            return validated.Error;
        return await db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var lines = await ResolveAsync(validated.Value, null, ct);
            if (!lines.IsSuccess)
                return lines.Error;
            var v = validated.Value;
            var invoice = Invoice.Create(v.CustomerId, v.IssueDate, v.DeliveryDate, v.DueDate, v.PaymentMethodId, v.WarehouseId,
                v.Reference, v.Note, lines.Value, clock.UtcNow, ActorKeyId());
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(invoice, ct);
        }, cancellationToken);
    }

    public async Task<Result<InvoiceDto>> ReplaceAsync(Guid id, InvoiceInput input, CancellationToken cancellationToken = default)
    {
        var validated = Validate(input);
        if (!validated.IsSuccess)
            return validated.Error;
        return await db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var invoice = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
                return NotFound();
            if (invoice.Status != InvoiceStatus.Draft)
                return NotDraft(invoice, "changed");
            var lines = await ResolveAsync(validated.Value, invoice, ct);
            if (!lines.IsSuccess)
                return lines.Error;
            var v = validated.Value;
            var removed = invoice.Replace(v.CustomerId, v.IssueDate, v.DeliveryDate, v.DueDate, v.PaymentMethodId, v.WarehouseId,
                v.Reference, v.Note, lines.Value, clock.UtcNow, ActorKeyId());
            db.InvoiceLines.RemoveRange(removed);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(invoice, ct);
        }, cancellationToken);
    }

    public Task<Result<InvoiceDeleted>> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<InvoiceDeleted>>(async ct =>
        {
            var invoice = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
                return NotFound();
            if (invoice.Status != InvoiceStatus.Draft)
                return NotDraft(invoice, "deleted (cancel an issued invoice instead)");
            db.InvoiceLines.RemoveRange(invoice.Lines);
            db.Invoices.Remove(invoice);
            await db.SaveChangesAsync(ct);
            return new InvoiceDeleted();
        }, cancellationToken);

    // ---- issue and cancel

    /// <summary>
    /// Order of checks: exists -> draft -> masters still active -> customer rule -> date rule -> stock (with
    /// <c>invoice.issueMovesStock</c>). The number is taken last; a refusal saves nothing, so no number is lost.
    /// </summary>
    public Task<Result<InvoiceDto>> IssueAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var invoice = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
                return NotFound();
            if (invoice.Status != InvoiceStatus.Draft)
                return NotDraft(invoice, "issued");
            if (await InactiveMasterAsync(invoice, ct) is { } inactive)
                return inactive;

            var errors = new ValidationErrors();
            var customerRequired = tenantRules[RuleRegistry.InvoiceCustomerRequired];
            if (invoice.CustomerId is null && customerRequired)
                errors.Add("customerId", $"customerId is required to issue an invoice: this company's rule {RuleRegistry.InvoiceCustomerRequired.Key} is true.",
                    RuleRegistry.InvoiceCustomerRequired, customerRequired);
            if (await BackdatedAsync(invoice.IssueDate, tenantRules, errors, ct))
                return errors.ToError();
            if (errors.Any)
                return errors.ToError();

            var now = clock.UtcNow;
            var number = await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.Invoice, ct);
            Guid? documentId = null;
            if (tenantRules[RuleRegistry.InvoiceIssueMovesStock])
            {
                var (posted, refused) = await PostStockAsync(invoice, number, tenantRules, ct);
                if (refused is not null)
                    return refused;
                documentId = posted;
            }
            invoice.Issue(number, documentId, now, ActorKeyId());
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(invoice, ct);
        }, cancellationToken);

    /// <summary>Issues the cancelling invoice of an issued one (negated lines, next number) and reverses its stock issue, if any.</summary>
    public async Task<Result<InvoiceDto>> CancelAsync(Guid id, CancelInvoiceInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var date = Date(errors, "issueDate", input.IssueDate);
        var note = Note(errors, input.Note);
        if (errors.Any)
            return errors.ToError();

        return await db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var original = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (original is null)
                return NotFound();
            if (original.Status != InvoiceStatus.Issued)
                return AppError.InvalidState($"The invoice is {original.Status.ToName()}: only an issued invoice can be cancelled.");
            if (original.IsCancellation)
                return AppError.InvalidState("A cancelling invoice cannot be cancelled.");
            var issueDate = date ?? Today();
            var dateErrors = new ValidationErrors();
            if (issueDate < original.IssueDate)
                return AppError.Validation("issueDate", "The cancelling invoice cannot be dated before the invoice it cancels.");
            if (await BackdatedAsync(issueDate, tenantRules, dateErrors, ct))
                return dateErrors.ToError();

            Guid? reversalId = null;
            if (original.StockDocumentId is { } documentId)
            {
                var document = await db.StockDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == documentId, ct);
                var reversed = await stock.ReverseWithinLockAsync(document, new StockReversalValues(issueDate, note), tenantRules, ct);
                if (!reversed.IsSuccess)
                    return reversed.Error!;
                reversalId = reversed.Value.Id;
            }
            var number = await StockReads.NextNumberAsync(db, TenantId(), DocumentSeries.Invoice, ct);
            var cancellation = Invoice.CancellationOf(original, number, issueDate, note, reversalId, clock.UtcNow, ActorKeyId());
            db.Invoices.Add(cancellation);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(cancellation, ct);
        }, cancellationToken);
    }

    // ---- payments

    public async Task<Result<InvoiceDto>> AddPaymentAsync(Guid id, InvoicePaymentInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var date = Date(errors, "paymentDate", input.PaymentDate);
        if (input.Amount is not { } amount || amount <= 0 || decimal.Round(amount, InvoiceRules.AmountDecimals) != amount || amount > InvoiceRules.MaxAmount)
            errors.Add("amount", "amount is required: a number greater than 0 with at most 2 decimal places.");
        var methodId = OptionalUuid(errors, "paymentMethodId", input.PaymentMethodId);
        var note = Note(errors, input.Note);
        if (errors.Any)
            return errors.ToError();

        return await db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var tenantRules = await rules.ReadAsync(ct);
            var invoice = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
                return NotFound();
            if (invoice.Status != InvoiceStatus.Issued || invoice.IsCancellation || invoice.TotalAmount <= 0)
                return AppError.InvalidState("Payments are recorded only on an issued invoice with something to pay; not on a draft, a cancelled or a cancelling invoice.");
            if (methodId is { } m && await CheckActiveAsync(db.PaymentMethods.Where(p => p.Id == m).Select(p => (bool?)p.IsActive), "paymentMethodId", m, ct) is { } refused)
                return refused;
            var overpaymentAllowed = tenantRules[RuleRegistry.InvoiceOverpaymentAllowed];
            if (!overpaymentAllowed && invoice.PaidAmount + input.Amount!.Value > invoice.TotalAmount)
            {
                var payErrors = new ValidationErrors();
                payErrors.Add("amount", $"amount is more than is still owed ({Money(invoice.TotalAmount - invoice.PaidAmount)}): this company's rule {RuleRegistry.InvoiceOverpaymentAllowed.Key} is false.",
                    RuleRegistry.InvoiceOverpaymentAllowed, overpaymentAllowed);
                return payErrors.ToError();
            }
            var payment = invoice.AddPayment(date ?? Today(), input.Amount!.Value, methodId, note, clock.UtcNow, ActorKeyId());
            db.InvoicePayments.Add(payment);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(invoice, ct);
        }, cancellationToken);
    }

    public Task<Result<InvoiceDto>> DeletePaymentAsync(Guid id, Guid paymentId, CancellationToken cancellationToken = default) =>
        db.SerializedPerTenantAsync<Result<InvoiceDto>>(async ct =>
        {
            var invoice = await Load(db.Invoices).SingleOrDefaultAsync(i => i.Id == id, ct);
            if (invoice is null)
                return NotFound();
            var payment = invoice.Payments.FirstOrDefault(p => p.Id == paymentId);
            if (payment is null)
                return AppError.NotFound("Payment not found on this invoice.");
            invoice.RemovePayment(payment, clock.UtcNow, ActorKeyId());
            db.InvoicePayments.Remove(payment);
            await db.SaveChangesAsync(ct);
            return await ToDtoAsync(invoice, ct);
        }, cancellationToken);

    // ---- string-id overloads, for MCP tools

    public Task<Result<InvoiceDto>> ReplaceAsync(ReplaceInvoiceToolInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, id => ReplaceAsync(id, input.ToInput(), cancellationToken));

    public Task<Result<InvoiceDeleted>> DeleteAsync(string? id, CancellationToken cancellationToken = default) =>
        ById(id, parsed => DeleteAsync(parsed, cancellationToken));

    public Task<Result<InvoiceDto>> IssueAsync(string? id, CancellationToken cancellationToken = default) =>
        ById(id, parsed => IssueAsync(parsed, cancellationToken));

    public Task<Result<InvoiceDto>> CancelAsync(CancelInvoiceToolInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, id => CancelAsync(id, input.ToInput(), cancellationToken));

    public Task<Result<InvoiceDto>> AddPaymentAsync(AddInvoicePaymentToolInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, id => AddPaymentAsync(id, input.ToInput(), cancellationToken));

    public Task<Result<InvoiceDto>> DeletePaymentAsync(InvoicePaymentAddressInput input, CancellationToken cancellationToken = default) =>
        ById(input.Id, id => RecordAddress.Id(input.PaymentId, "Payment not found on this invoice.", out var paymentId, "paymentId") is { } error
            ? Task.FromResult<Result<InvoiceDto>>(error)
            : DeletePaymentAsync(id, paymentId, cancellationToken));

    private static Task<Result<T>> ById<T>(string? id, Func<Guid, Task<Result<T>>> run) =>
        RecordAddress.Id(id, NotFoundDetail, out var parsed) is { } error ? Task.FromResult<Result<T>>(error) : run(parsed);

    // ---- validation and resolution

    private static Result<Values> Validate(InvoiceInput input)
    {
        var errors = new ValidationErrors();
        var customerId = OptionalUuid(errors, "customerId", input.CustomerId);
        var issueDate = Date(errors, "issueDate", input.IssueDate);
        var deliveryDate = Date(errors, "deliveryDate", input.DeliveryDate);
        var dueDate = Date(errors, "dueDate", input.DueDate);
        var paymentMethodId = OptionalUuid(errors, "paymentMethodId", input.PaymentMethodId);
        var warehouseId = OptionalUuid(errors, "warehouseId", input.WarehouseId);
        if (!OptionalTextRules.TryNormalize(input.Reference, Invoice.ReferenceMaxLength, out var reference))
            errors.Add("reference", $"reference must be at most {Invoice.ReferenceMaxLength} characters on a single line.");
        var note = Note(errors, input.Note);

        var lines = new List<LineValues>();
        if (input.Lines is not { Count: >= 1 and <= InvoiceRules.MaxLines } given)
            errors.Add("lines", $"lines is required: 1 to {InvoiceRules.MaxLines} lines.");
        else
        {
            for (var i = 0; i < given.Count; i++)
            {
                var l = given[i];
                if (l is null)
                {
                    errors.Add($"lines[{i}]", "A line must be an object with articleId and quantity.");
                    continue;
                }
                var key = (string field) => $"lines[{i}].{field}";
                Guid articleId = default;
                if (!Guid.TryParse(l.ArticleId, out articleId))
                    errors.Add(key("articleId"), string.IsNullOrEmpty(l.ArticleId) ? "articleId is required." : "articleId must be the id (UUID) of an article.");
                if (l.Quantity is not { } quantity || !QuantityRules.IsValid(quantity))
                    errors.Add(key("quantity"), $"quantity is required: a number greater than 0, at most {QuantityRules.Max.ToString(CultureInfo.InvariantCulture)}, with at most {QuantityRules.DecimalPlaces} decimal places.");
                if (l.UnitPrice is { } price && !PriceRules.IsValid(price))
                    errors.Add(key("unitPrice"), $"unitPrice must be 0 or more, with at most {PriceRules.DecimalPlaces} decimal places.");
                if (l.DiscountPercent is { } discount && !InvoiceRules.IsValidDiscount(discount))
                    errors.Add(key("discountPercent"), "discountPercent must be from 0 to 100, with at most 2 decimal places.");
                Guid? taxRateId = null;
                if (l.TaxRateId is not null)
                {
                    if (Guid.TryParse(l.TaxRateId, out var t)) taxRateId = t;
                    else errors.Add(key("taxRateId"), "taxRateId must be the id (UUID) of a tax rate.");
                }
                if (!OptionalTextRules.TryNormalize(l.Description, InvoiceRules.DescriptionMaxLength, out var description))
                    errors.Add(key("description"), $"description must be at most {InvoiceRules.DescriptionMaxLength} characters on a single line.");
                lines.Add(new LineValues(i, articleId, l.Quantity ?? 0, l.UnitPrice, l.DiscountPercent ?? 0, taxRateId, description));
                if (l.Quantity is { } q && l.UnitPrice is { } p && InvoiceRules.LineNet(q, p, l.DiscountPercent ?? 0) > InvoiceRules.MaxAmount)
                    errors.Add(key("quantity"), "The line's amount is too large.");
            }
        }
        var issue = issueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (dueDate is { } due && due < issue)
            errors.Add("dueDate", "dueDate cannot be before issueDate.");
        return errors.Any
            ? errors.ToError()
            : new Values(customerId, issue, deliveryDate, dueDate, paymentMethodId, warehouseId, reference, note, lines);
    }

    /// <summary>
    /// The references of a create or a replace, then the defaults of each line: the article's tax rate (when it
    /// has exactly one) and its sales price without tax. An inactive record may stay where the draft already has it.
    /// </summary>
    private async Task<Result<IReadOnlyList<InvoiceLineEntry>>> ResolveAsync(Values v, Invoice? existing, CancellationToken ct)
    {
        if (v.CustomerId is { } customerId)
        {
            var customer = await db.Partners.AsNoTracking().Where(p => p.Id == customerId).Select(p => new { p.IsActive, p.IsCustomer }).SingleOrDefaultAsync(ct);
            if (customer is null)
                return AppError.ReferenceNotFound("customerId", customerId);
            if (!customer.IsActive && existing?.CustomerId != customerId)
                return AppError.ReferenceInactive("customerId", customerId);
            if (!customer.IsCustomer)
                return new AppError(ErrorCodes.PartnerRoleMissing,
                    "The partner named by customerId is not a customer: an invoice needs a partner with isCustomer = true.",
                    new Dictionary<string, string[]> { ["customerId"] = [$"The partner with id '{customerId}' does not have isCustomer."] });
        }
        if (v.PaymentMethodId is { } methodId && existing?.PaymentMethodId != methodId
            && await CheckActiveAsync(db.PaymentMethods.Where(p => p.Id == methodId).Select(p => (bool?)p.IsActive), "paymentMethodId", methodId, ct) is { } method)
            return method;
        if (v.WarehouseId is { } warehouseId && existing?.WarehouseId != warehouseId
            && await CheckActiveAsync(db.Warehouses.Where(w => w.Id == warehouseId).Select(w => (bool?)w.IsActive), "warehouseId", warehouseId, ct) is { } warehouse)
            return warehouse;

        var articleIds = v.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var articles = await db.Articles.AsNoTracking().Where(a => articleIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.IsActive }).ToDictionaryAsync(a => a.Id, ct);
        var prices = await db.ArticleSalesData.AsNoTracking().Where(s => articleIds.Contains(s.ArticleId))
            .ToDictionaryAsync(s => s.ArticleId, s => s.SalesPrice, ct);
        var articleRates = (await db.ArticleTaxRates.AsNoTracking().Where(r => articleIds.Contains(r.ArticleId)).ToListAsync(ct))
            .GroupBy(r => r.ArticleId).ToDictionary(g => g.Key, g => g.Select(r => r.TaxRateId).ToList());
        var rateIds = v.Lines.Where(l => l.TaxRateId is not null).Select(l => l.TaxRateId!.Value).Concat(articleRates.Values.SelectMany(x => x)).Distinct().ToList();
        var rates = await db.TaxRates.AsNoTracking().Where(t => rateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Rate, t.IsActive }).ToDictionaryAsync(t => t.Id, ct);
        var assigned = existing?.Lines.Select(l => (l.ArticleId, l.TaxRateId)).ToHashSet() ?? [];

        var errors = new ValidationErrors();
        var entries = new List<InvoiceLineEntry>();
        foreach (var line in v.Lines)
        {
            var key = (string field) => $"lines[{line.Index}].{field}";
            if (!articles.TryGetValue(line.ArticleId, out var article))
                return AppError.ReferenceNotFound(key("articleId"), line.ArticleId);
            if (!article.IsActive && !assigned.Any(a => a.ArticleId == line.ArticleId))
                return AppError.ReferenceInactive(key("articleId"), line.ArticleId);

            Guid? taxRateId = line.TaxRateId;
            if (taxRateId is null)
            {
                var own = articleRates.GetValueOrDefault(line.ArticleId) ?? [];
                if (own.Count == 1)
                    taxRateId = own[0];
                else
                {
                    errors.Add(key("taxRateId"), own.Count == 0
                        ? "taxRateId is required: the article has no tax rate in its sales data."
                        : "taxRateId is required: the article has several tax rates; choose the one this line is taxed at.");
                    continue;
                }
            }
            if (!rates.TryGetValue(taxRateId.Value, out var rate))
                return AppError.ReferenceNotFound(key("taxRateId"), taxRateId.Value);
            if (!rate.IsActive && !assigned.Contains((line.ArticleId, taxRateId.Value)))
                return AppError.ReferenceInactive(key("taxRateId"), taxRateId.Value);

            var unitPrice = line.UnitPrice;
            if (unitPrice is null)
            {
                if (prices.GetValueOrDefault(line.ArticleId) is not { } gross)
                {
                    errors.Add(key("unitPrice"), "unitPrice is required: the article has no sales price.");
                    continue;
                }
                unitPrice = InvoiceRules.NetOfGross(gross, rate.Rate);
            }
            entries.Add(new InvoiceLineEntry(line.ArticleId, line.Description ?? article.Name, line.Quantity, unitPrice.Value, line.Discount, taxRateId.Value, rate.Rate));
        }
        return errors.Any ? errors.ToError() : entries;
    }

    private async Task<AppError?> InactiveMasterAsync(Invoice invoice, CancellationToken ct)
    {
        if (invoice.CustomerId is { } c && await CheckActiveAsync(db.Partners.Where(p => p.Id == c).Select(p => (bool?)p.IsActive), "customerId", c, ct) is { } customer)
            return customer;
        var ids = invoice.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var inactive = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id) && !a.IsActive).Select(a => a.Id).FirstOrDefaultAsync(ct);
        if (inactive != Guid.Empty)
            return new AppError(ErrorCodes.ReferenceInactive, "An article on the invoice is inactive: reactivate it or change the draft before issuing.",
                new Dictionary<string, string[]> { [$"lines[{invoice.Lines.First(l => l.ArticleId == inactive).LineNo - 1}].articleId"] = [$"The record with id '{inactive}' is inactive."] });
        return null;
    }

    private static async Task<AppError?> CheckActiveAsync(IQueryable<bool?> isActive, string field, Guid id, CancellationToken ct) =>
        await isActive.SingleOrDefaultAsync(ct) switch
        {
            null => AppError.ReferenceNotFound(field, id),
            false => AppError.ReferenceInactive(field, id),
            _ => null,
        };

    /// <summary>With <c>invoice.backdatingAllowed</c> false: true (and an error added) when an issued invoice is dated later.</summary>
    private async Task<bool> BackdatedAsync(DateOnly date, TenantRules tenantRules, ValidationErrors errors, CancellationToken ct)
    {
        var allowed = tenantRules[RuleRegistry.InvoiceBackdatingAllowed];
        if (allowed)
            return false;
        var latest = await db.Invoices.AsNoTracking().Where(i => i.Number != null).MaxAsync(i => (DateOnly?)i.IssueDate, ct);
        if (latest is { } last && date < last)
        {
            errors.Add("issueDate", $"issueDate {date:yyyy-MM-dd} is before the latest issued invoice ({last:yyyy-MM-dd}): this company's rule {RuleRegistry.InvoiceBackdatingAllowed.Key} is false.",
                RuleRegistry.InvoiceBackdatingAllowed, allowed);
            return true;
        }
        return false;
    }

    /// <summary>The stock issue of an invoice's stock articles, posted; its errors keyed by the invoice's lines.</summary>
    private async Task<(Guid? Id, AppError? Error)> PostStockAsync(Invoice invoice, string number, TenantRules tenantRules, CancellationToken ct)
    {
        var ids = invoice.Lines.Select(l => l.ArticleId).Distinct().ToList();
        var stocked = await db.Articles.AsNoTracking().Where(a => ids.Contains(a.Id) && a.Type == ArticleType.Stock)
            .ToDictionaryAsync(a => a.Id, a => a.BaseUnitId, ct);
        var lines = invoice.Lines.Where(l => stocked.ContainsKey(l.ArticleId)).ToList();
        if (lines.Count == 0)
            return (null, null);
        var warehouseId = invoice.WarehouseId ?? await db.Warehouses.Where(w => w.IsDefault).Select(w => (Guid?)w.Id).SingleOrDefaultAsync(ct);
        if (warehouseId is null)
            return (null, AppError.Validation("warehouseId", "warehouseId is required: the invoice issues goods from stock and the company has no default warehouse."));
        var document = StockDocument.Create(StockDocumentType.Issue, invoice.IssueDate, warehouseId.Value, null, number, $"Goods of invoice {number}.",
            lines.Select(l => new StockLineEntry(l.ArticleId, stocked[l.ArticleId], l.Quantity)).ToList(), clock.UtcNow, ActorKeyId(),
            partnerId: invoice.CustomerId);
        db.StockDocuments.Add(document);
        if (await stock.PostWithinLockAsync(document, tenantRules, ct) is { } refused)
        {
            // Keyed by the document's lines; translate to the invoice's.
            var errors = refused.Errors?.ToDictionary(
                e => System.Text.RegularExpressions.Regex.Replace(e.Key, @"^lines\[(\d+)\]\.\w+$",
                    m => $"lines[{lines[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)].LineNo - 1}].quantity"),
                e => e.Value);
            return (null, refused with { Errors = errors });
        }
        return (document.Id, null);
    }

    // ---- representation

    private static IQueryable<Invoice> Load(IQueryable<Invoice> invoices) => invoices.Include(i => i.Lines).Include(i => i.Payments);

    private async Task<InvoiceDto> ToDtoAsync(Invoice invoice, CancellationToken ct)
    {
        var lines = invoice.Lines;
        var articleIds = lines.Select(l => l.ArticleId).Distinct().ToList();
        var articles = await (from a in db.Articles.AsNoTracking()
                              join u in db.UnitsOfMeasure on a.BaseUnitId equals u.Id
                              where articleIds.Contains(a.Id)
                              select new { Article = new ReferenceSummary(a.Id, a.Code, a.Name), Unit = new ReferenceSummary(u.Id, u.Code, u.Name) })
            .ToDictionaryAsync(x => x.Article.Id, ct);
        var rateIds = lines.Select(l => l.TaxRateId).Distinct().ToList();
        var rates = await db.TaxRates.AsNoTracking().Where(t => rateIds.Contains(t.Id))
            .Select(t => new TaxRateSummary(t.Id, t.Code, t.Name, t.Rate)).ToDictionaryAsync(t => t.Id, ct);
        var methodIds = invoice.Payments.Select(p => p.PaymentMethodId).Append(invoice.PaymentMethodId).OfType<Guid>().Distinct().ToList();
        var methods = await db.PaymentMethods.AsNoTracking().Where(m => methodIds.Contains(m.Id))
            .Select(m => new ReferenceSummary(m.Id, m.Code, m.Name)).ToDictionaryAsync(m => m.Id, ct);
        var customer = invoice.CustomerId is { } c
            ? await db.Partners.AsNoTracking().Where(p => p.Id == c).Select(p => new ReferenceSummary(p.Id, p.Code, p.Name)).SingleOrDefaultAsync(ct)
            : null;
        var warehouse = invoice.WarehouseId is { } w
            ? await db.Warehouses.AsNoTracking().Where(x => x.Id == w).Select(x => new ReferenceSummary(x.Id, x.Code, x.Name)).SingleOrDefaultAsync(ct)
            : null;
        var document = invoice.StockDocumentId is { } d
            ? await db.StockDocuments.AsNoTracking().Where(x => x.Id == d).Select(x => new StockDocumentLinkDto(x.Id, x.Number!)).SingleOrDefaultAsync(ct)
            : null;
        var links = new[] { invoice.CancellationOfId, invoice.CancelledById }.OfType<Guid>().ToList();
        var linked = await db.Invoices.AsNoTracking().Where(i => links.Contains(i.Id)).Select(i => new InvoiceLinkDto(i.Id, i.Number!)).ToDictionaryAsync(i => i.Id, ct);

        return new InvoiceDto(
            invoice.Id, invoice.Status.ToName(), invoice.Number, customer, invoice.IssueDate, invoice.DeliveryDate, invoice.DueDate,
            invoice.PaymentMethodId is { } pm ? methods.GetValueOrDefault(pm) : null, warehouse, invoice.Reference, invoice.Note,
            invoice.NetAmount, invoice.TaxAmount, invoice.TotalAmount, invoice.PaidAmount, Open(invoice), PaymentStatusOf(invoice),
            InvoiceRules.Taxes(lines.Select(l => (l.TaxPercent, l.NetAmount))).Select(t => new InvoiceTaxDto(t.TaxPercent, t.NetAmount, t.TaxAmount)).ToList(),
            lines.Select(l => new InvoiceLineDto(l.LineNo, articles[l.ArticleId].Article, articles[l.ArticleId].Unit, l.Description, l.Quantity, l.UnitPrice,
                l.DiscountPercent, rates[l.TaxRateId], l.TaxPercent, l.NetAmount)).ToList(),
            invoice.Payments.OrderBy(p => p.PaymentDate).ThenBy(p => p.CreatedAt)
                .Select(p => new InvoicePaymentDto(p.Id, p.PaymentDate, p.Amount, p.PaymentMethodId is { } m ? methods.GetValueOrDefault(m) : null, p.Note, p.CreatedAt, p.CreatedBy)).ToList(),
            document,
            invoice.CancellationOfId is { } of ? linked.GetValueOrDefault(of) : null,
            invoice.CancelledById is { } by ? linked.GetValueOrDefault(by) : null,
            invoice.CreatedAt, invoice.UpdatedAt, invoice.CreatedBy, invoice.UpdatedBy, invoice.IssuedAt, invoice.IssuedBy);
    }

    private static decimal Open(Invoice i) =>
        PaymentStatusOf(i) == PaymentStatusNames.None ? 0 : Math.Max(0, i.TotalAmount - i.PaidAmount);

    private static string PaymentStatusOf(Invoice i) => InvoiceRules.PaymentStatus(i.Status, i.CancellationOfId is not null, i.TotalAmount, i.PaidAmount);

    // ---- small helpers

    private static Guid? OptionalUuid(ValidationErrors errors, string field, string? input)
    {
        if (input is null)
            return null;
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add(field, $"{field} must be the id (UUID) of a record, or null.");
        return null;
    }

    private static DateOnly? Date(ValidationErrors errors, string field, string? input)
    {
        if (input is null)
            return null;
        if (DateOnly.TryParseExact(input, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        errors.Add(field, $"{field} must be a calendar date written as YYYY-MM-DD.");
        return null;
    }

    private static string? Note(ValidationErrors errors, string? note)
    {
        if (!NoteRules.TryNormalize(note, Invoice.NoteMaxLength, out var normalized))
            errors.Add("note", $"note must be at most {Invoice.NoteMaxLength} characters and contain no control characters other than line breaks and tabs.");
        return normalized;
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow);

    private static AppError NotDraft(Invoice invoice, string attempted) =>
        AppError.InvalidState($"The invoice is {invoice.Status.ToName()}: only a draft can be {attempted}.");

    private Guid TenantId() => context.TenantId ?? throw new InvalidOperationException("Invoices are kept by a tenant.");

    private Guid ActorKeyId() => context.ApiKeyId ?? throw new InvalidOperationException("Invoices are written with a tenant API key.");

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);
}

using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Inventory;

namespace Xerp.Application.Agent;

/// <summary>
/// Spec A01, 2.3: what this company still needs before it is set up, computed from the tenant's data - never stored.
/// Each open item names the tool to call and the <c>guide</c> topic that explains it.
/// </summary>
public sealed class SetupStatusOperations(IXerpDb db)
{
    public async Task<Result<SetupStatusDto>> GetAsync(CancellationToken ct = default)
    {
        var profile = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(ct);
        var warehouses = await db.Warehouses.AsNoTracking().Select(w => new { w.Code, w.Name, w.IsDefault }).ToListAsync(ct);
        var taxRates = await db.TaxRates.CountAsync(ct);
        var paymentMethods = await db.PaymentMethods.CountAsync(ct);
        var units = await db.UnitsOfMeasure.CountAsync(ct);
        var sellable = await db.ArticleSalesData.CountAsync(
            d => d.SalesPrice != null && db.ArticleTaxRates.Any(t => t.ArticleId == d.ArticleId), ct);
        var customers = await db.Partners.CountAsync(p => p.IsCustomer && p.IsActive, ct);
        var suppliers = await db.Partners.CountAsync(p => p.IsSupplier && p.IsActive, ct);
        var users = await db.Users.CountAsync(u => u.IsActive, ct);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile?.TaxId)) missing.Add("OIB (taxId)");
        if (string.IsNullOrWhiteSpace(profile?.AddressLine1) || string.IsNullOrWhiteSpace(profile?.City)) missing.Add("address");
        if (string.IsNullOrWhiteSpace(profile?.Iban)) missing.Add("IBAN");
        var ownWarehouse = warehouses.Count > 1 || warehouses.Any(w => w.IsDefault && (w.Code != Warehouse.DefaultCode || w.Name != Warehouse.DefaultName));

        var items = new List<SetupItemDto>
        {
            Item("companyProfile", profile is not null && missing.Count == 0,
                missing.Count == 0 ? "Name, OIB, address and IBAN are filled." : $"Missing: {string.Join(", ", missing)}.",
                "company_set (guide topic: company-setup)"),
            Item("warehouses", ownWarehouse,
                ownWarehouse ? $"{warehouses.Count} warehouse(s)." : "Only the default warehouse CENTRAL exists, unchanged.",
                "warehouse_create or warehouse_update (guide topic: company-setup)"),
            Item("taxRates", taxRates > 0, $"{taxRates} tax rate(s).", "tax_rate_create (guide topic: articles-and-prices)"),
            Item("paymentMethods", paymentMethods > 0, $"{paymentMethods} payment method(s).", "payment_method_create (guide topic: company-setup)"),
            Item("unitsOfMeasure", units > 0, $"{units} unit(s) of measure.", "uom_create (guide topic: articles-and-prices)"),
            Item("articles", sellable > 0, $"{sellable} article(s) with a sales price and a tax rate.",
                "article_create, then article_sales_data_set (guide topic: articles-and-prices)"),
            Item("customer", customers > 0, $"{customers} customer(s).", "partner_create with isCustomer true (guide topic: partners)"),
            Item("supplier", suppliers > 0, $"{suppliers} supplier(s).", "partner_create with isSupplier true (guide topic: partners)"),
            Item("rulesReviewed", profile?.RulesReviewedAt is not null,
                profile?.RulesReviewedAt is { } at ? $"Reviewed at {at:O}." : "The business rules are still the shipped defaults, never reviewed with the user.",
                "rule_profile_list, then rule_set_many or setup_mark_rules_reviewed (guide topic: business-rules)"),
            Item("users", users > 0, $"{users} user(s) who can sign in.", "Create a user with POST /users (guide topic: company-setup)"),
            // Backups are monitored from spec R01 on; until then the answer is unknown and the item is not done.
            Item("backups", false, "unknown: backup monitoring does not exist yet (spec R01).", "Ask whoever runs the server whether backups are running (guide topic: month-end)"),
        };
        var done = items.Count(i => i.Done);
        return new SetupStatusDto(done == items.Count, done, items.Count, items);
    }

    private static SetupItemDto Item(string key, bool done, string detail, string nextStep) => new(key, done, detail, done ? "" : nextStep);
}

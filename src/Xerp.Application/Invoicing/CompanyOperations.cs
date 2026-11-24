using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Common;
using Xerp.Domain.Rules;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Invoicing;

/// <summary>The company's own data for its documents (spec I01): read, and set as a whole.</summary>
public sealed class CompanyOperations(IXerpDb db, ITenantContext context, ICurrentTenantReader currentTenant, IClock clock)
{
    public async Task<Result<CompanyProfileDto>> GetAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await currentTenant.ReadAsync(cancellationToken);
        if (tenant is null)
            return AppError.Unauthenticated();
        var profile = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return ToDto(profile, tenant);
    }

    public async Task<Result<CompanyProfileDto>> SetAsync(CompanyProfileInput input, CancellationToken cancellationToken = default)
    {
        var tenant = await currentTenant.ReadAsync(cancellationToken);
        if (tenant is null || context.ApiKeyId is not { } actor)
            return AppError.Unauthenticated();
        var errors = new ValidationErrors();
        var legalName = errors.Name(input.LegalName, "legalName");
        var taxId = Text(errors, "taxId", input.TaxId, CompanyProfile.TaxIdMaxLength);
        var address = FieldRules.AddressOf(errors, input, replace: false, new RequiredFields(TenantRules.Defaults, new Dictionary<string, RuleDefinition>()));
        var iban = Text(errors, "iban", input.Iban?.Replace(" ", ""), CompanyProfile.IbanMaxLength)?.ToUpperInvariant();
        var email = Text(errors, "email", input.Email, CompanyProfile.ContactMaxLength);
        var phone = Text(errors, "phone", input.Phone, CompanyProfile.ContactMaxLength);
        var website = Text(errors, "website", input.Website, CompanyProfile.ContactMaxLength);
        if (!NoteRules.TryNormalize(input.InvoiceFooter, CompanyProfile.FooterMaxLength, out var footer))
            errors.Add("invoiceFooter", $"invoiceFooter must be at most {CompanyProfile.FooterMaxLength} characters.");
        if (errors.Any)
            return errors.ToError();

        return await db.SerializedPerTenantAsync<Result<CompanyProfileDto>>(async ct =>
        {
            var profile = await db.CompanyProfiles.SingleOrDefaultAsync(ct);
            if (profile is null)
            {
                profile = CompanyProfile.Create(clock.UtcNow, actor);
                db.CompanyProfiles.Add(profile);
            }
            profile.Set(legalName, taxId, address, iban, email, phone, website, footer, clock.UtcNow, actor);
            await db.SaveChangesAsync(ct);
            return ToDto(profile, tenant);
        }, cancellationToken);
    }

    /// <summary>Spec A01: records that the company's business rules were looked at (also done by <c>rule_set_many</c>). Repeating it moves the time.</summary>
    public async Task<Result<RulesReviewedDto>> MarkRulesReviewedAsync(CancellationToken cancellationToken = default)
    {
        var tenant = await currentTenant.ReadAsync(cancellationToken);
        if (tenant is null || context.ApiKeyId is not { } actor)
            return AppError.Unauthenticated();
        return await db.SerializedPerTenantAsync<Result<RulesReviewedDto>>(async ct =>
        {
            var now = clock.UtcNow;
            var profile = await db.CompanyProfiles.SingleOrDefaultAsync(ct);
            if (profile is null)
            {
                profile = CompanyProfile.CreateForReview(tenant.Name, now, actor);
                db.CompanyProfiles.Add(profile);
            }
            profile.MarkRulesReviewed(now);
            await db.SaveChangesAsync(ct);
            return new RulesReviewedDto(now);
        }, cancellationToken);
    }

    private static string? Text(ValidationErrors errors, string field, string? value, int maxLength)
    {
        if (OptionalTextRules.TryNormalize(value, maxLength, out var text))
            return text;
        errors.Add(field, $"{field} must be at most {maxLength} characters on a single line.");
        return null;
    }

    private static CompanyProfileDto ToDto(CompanyProfile? p, CurrentTenant tenant) => p is null
        ? new CompanyProfileDto(tenant.Name, null, null, null, null, null, null, null, null, null, null, null, null, tenant.Code, null, null)
        : new CompanyProfileDto(p.LegalName, p.TaxId, p.AddressLine1, p.AddressLine2, p.PostalCode, p.City, p.Region, p.CountryCode,
            p.Iban, p.Email, p.Phone, p.Website, p.InvoiceFooter, tenant.Code, p.UpdatedAt, p.UpdatedBy, p.RulesReviewedAt);
}

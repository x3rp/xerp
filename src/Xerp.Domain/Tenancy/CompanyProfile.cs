using Xerp.Domain.Common;

namespace Xerp.Domain.Tenancy;

/// <summary>
/// What a tenant's documents say about the company itself (spec I01): the legal name, OIB, address, bank account
/// and contacts printed on every invoice, and a footer text. At most one per tenant; absent until first saved.
/// </summary>
public sealed class CompanyProfile : ITenantOwned
{
    public const int TaxIdMaxLength = 20;
    public const int IbanMaxLength = 34;
    public const int ContactMaxLength = 200;
    public const int FooterMaxLength = 2000;

    private CompanyProfile() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string LegalName { get; private set; } = "";
    public string? TaxId { get; private set; }
    public string? AddressLine1 { get; private set; }
    public string? AddressLine2 { get; private set; }
    public string? PostalCode { get; private set; }
    public string? City { get; private set; }
    public string? Region { get; private set; }
    public string? CountryCode { get; private set; }
    public string? Iban { get; private set; }
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Website { get; private set; }

    /// <summary>Printed under every invoice, e.g. whether the company is in the VAT system.</summary>
    public string? InvoiceFooter { get; private set; }

    public DateTime UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static CompanyProfile Create(DateTime now, Guid actorKeyId) => new() { Id = Guid.CreateVersion7(), UpdatedAt = now, UpdatedBy = actorKeyId };

    public void Set(
        string legalName, string? taxId, Address address, string? iban, string? email, string? phone, string? website, string? invoiceFooter,
        DateTime now, Guid actorKeyId)
    {
        LegalName = NameRules.Normalize(legalName, nameof(legalName));
        TaxId = taxId;
        AddressLine1 = address.Line1;
        AddressLine2 = address.Line2;
        PostalCode = address.PostalCode;
        City = address.City;
        Region = address.Region;
        CountryCode = address.CountryCode;
        Iban = iban;
        Email = email;
        Phone = phone;
        Website = website;
        InvoiceFooter = invoiceFooter;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

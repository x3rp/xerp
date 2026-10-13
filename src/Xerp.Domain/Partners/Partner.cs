using Xerp.Domain.Common;

namespace Xerp.Domain.Partners;

/// <summary>Field rules of a partner beyond the shared code, name and address rules (spec 004, R3, R15).</summary>
public static class PartnerRules
{
    public const int TaxIdMaxLength = 50;

    /// <summary>A partner is a customer, a supplier or both (ADR-0011, decision 1); whether one with neither may be saved is <see cref="RoleMissing"/>.</summary>
    public static bool HasRole(bool isCustomer, bool isSupplier) => isCustomer || isSupplier;

    /// <summary>
    /// Spec 013, R8: with <c>partner.roleRequired</c> true a partner without a role is refused; with false it
    /// may be saved, and is still no supplier and no customer where a document needs one.
    /// </summary>
    public static bool RoleMissing(bool roleRequired, bool isCustomer, bool isSupplier) => roleRequired && !HasRole(isCustomer, isSupplier);
}

/// <summary>A company or person the tenant sells to (customer), buys from (supplier), or both.</summary>
public sealed class Partner : ITenantOwned
{
    private Partner() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsCustomer { get; private set; }
    public bool IsSupplier { get; private set; }

    /// <summary>Free text without a format; not unique (ADR-0011, decisions 5 and 6).</summary>
    public string? TaxId { get; private set; }

    public Address Address { get; private set; } = Address.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static Partner Create(
        string code, string name, bool isCustomer, bool isSupplier, string? taxId, Address address, bool isActive,
        DateTime now, Guid actorKeyId)
    {
        var partner = new Partner { Id = Guid.CreateVersion7(), CreatedAt = now, CreatedBy = actorKeyId };
        partner.Replace(code, name, isCustomer, isSupplier, taxId, address, isActive, now, actorKeyId);
        return partner;
    }

    public void Replace(
        string code, string name, bool isCustomer, bool isSupplier, string? taxId, Address address, bool isActive,
        DateTime now, Guid actorKeyId)
    {
        // Validate everything before assigning anything, so a rejected replace leaves the partner untouched.
        var newCode = CodeRules.Normalize(code, nameof(code));
        var newName = NameRules.Normalize(name, nameof(name));
        var newTaxId = OptionalTextRules.Normalize(taxId, PartnerRules.TaxIdMaxLength, nameof(taxId));
        // Whether a partner needs a role is the tenant's rule partner.roleRequired (spec 013, R8), judged in
        // validation with the tenant's value: the entity holds what a tenant with the rule false may save.
        Code = newCode;
        Name = newName;
        IsCustomer = isCustomer;
        IsSupplier = isSupplier;
        TaxId = newTaxId;
        Address = address;
        IsActive = isActive;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

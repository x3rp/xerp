using Xerp.Domain.Common;

namespace Xerp.Domain.Sales;

/// <summary>
/// What the three sales masters of spec P01 share with a unit of measure (P01/R1): a code, a name, an active
/// flag and the audit members. Not an entity itself; each master is its own table.
/// </summary>
public abstract class CodedMaster : ITenantOwned
{
    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public string Code { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }

    protected void Init(string code, string name, bool isActive, DateTime now, Guid actorKeyId)
    {
        Id = Guid.CreateVersion7();
        CreatedAt = now;
        CreatedBy = actorKeyId;
        Change(code, name, isActive, now, actorKeyId);
    }

    protected void Change(string code, string name, bool isActive, DateTime now, Guid actorKeyId)
    {
        // Validate both before assigning either, so a rejected replace leaves the record untouched.
        var newCode = CodeRules.Normalize(code, nameof(code));
        var newName = NameRules.Normalize(name, nameof(name));
        Code = newCode;
        Name = newName;
        IsActive = isActive;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

/// <summary>A category of articles (spec P01): one level, a name to group articles by.</summary>
public sealed class ArticleCategory : CodedMaster
{
    private ArticleCategory() { }

    public static ArticleCategory Create(string code, string name, bool isActive, DateTime now, Guid actorKeyId)
    {
        var category = new ArticleCategory();
        category.Init(code, name, isActive, now, actorKeyId);
        return category;
    }

    public void Replace(string code, string name, bool isActive, DateTime now, Guid actorKeyId) =>
        Change(code, name, isActive, now, actorKeyId);
}

/// <summary>A tax rate (spec P01): a percentage that articles are taxed with. Nothing computes tax yet.</summary>
public sealed class TaxRate : CodedMaster
{
    private TaxRate() { }

    /// <summary>The percentage, 0 to 100 (P01, 6.2).</summary>
    public decimal Rate { get; private set; }

    /// <exception cref="ArgumentException">The rate breaks its rule.</exception>
    public static TaxRate Create(string code, string name, decimal rate, bool isActive, DateTime now, Guid actorKeyId)
    {
        var taxRate = new TaxRate();
        taxRate.Init(code, name, isActive, now, actorKeyId);
        taxRate.Rate = Valid(rate);
        return taxRate;
    }

    /// <summary>The rate may change (P01/R4): nothing stores a copy of it yet.</summary>
    /// <exception cref="ArgumentException">The rate breaks its rule.</exception>
    public void Replace(string code, string name, decimal rate, bool isActive, DateTime now, Guid actorKeyId)
    {
        var newRate = Valid(rate);
        Change(code, name, isActive, now, actorKeyId);
        Rate = newRate;
    }

    private static decimal Valid(decimal rate) =>
        SalesDataRules.IsValidRate(rate)
            ? rate
            : throw new ArgumentException("A tax rate is between 0 and 100 with at most 4 decimal places.", nameof(rate));
}

/// <summary>What a payment method is (P01, 6.2): later specs treat the kinds differently.</summary>
public enum PaymentMethodKind
{
    Cash,
    Card,
    Transfer,
    Other,
}

public static class PaymentMethodKindNames
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Transfer = "transfer";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Cash, Card, Transfer, Other];

    public static string ToName(this PaymentMethodKind kind) => kind switch
    {
        PaymentMethodKind.Cash => Cash,
        PaymentMethodKind.Card => Card,
        PaymentMethodKind.Transfer => Transfer,
        PaymentMethodKind.Other => Other,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static PaymentMethodKind Parse(string name) =>
        TryParse(name, out var kind) ? kind : throw new ArgumentException($"Unknown payment method kind '{name}'.", nameof(name));

    /// <summary>Exact, case-sensitive match (P01/R3): <c>Cash</c> is not a kind.</summary>
    public static bool TryParse(string? name, out PaymentMethodKind kind)
    {
        var index = name is null ? -1 : All.IndexOf(name);
        kind = index < 0 ? default : (PaymentMethodKind)index;
        return index >= 0;
    }

    private static int IndexOf(this IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        return -1;
    }
}

/// <summary>A payment method (spec P01): only a name and a kind here; nothing pays with it yet.</summary>
public sealed class PaymentMethod : CodedMaster
{
    private PaymentMethod() { }

    public PaymentMethodKind Kind { get; private set; }

    public static PaymentMethod Create(string code, string name, PaymentMethodKind kind, bool isActive, DateTime now, Guid actorKeyId)
    {
        var method = new PaymentMethod();
        method.Init(code, name, isActive, now, actorKeyId);
        method.Kind = kind;
        return method;
    }

    /// <summary>The kind may change (P01/R4): nothing freezes it yet.</summary>
    public void Replace(string code, string name, PaymentMethodKind kind, bool isActive, DateTime now, Guid actorKeyId)
    {
        Change(code, name, isActive, now, actorKeyId);
        Kind = kind;
    }
}

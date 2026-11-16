using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Domain.Production;

/// <summary>
/// One line of an article's bill of materials (spec W01, section 3): how much of a component goes into one
/// base unit of the article (the product). Both quantities are in base units - the component's and the
/// product's - so a bill never depends on unit conversions. An article has at most one bill: the set of its
/// lines, replaced as a whole. Lines have no id; their key is (tenant, article, line number).
/// </summary>
public sealed class BomComponent : ITenantOwned
{
    private BomComponent() { }

    public BomComponent(Guid articleId, int lineNo, Guid componentId, decimal quantity, DateTime now, Guid actorKeyId)
    {
        if (lineNo < 1)
            throw new ArgumentOutOfRangeException(nameof(lineNo));
        if (componentId == articleId)
            throw new ArgumentException("An article cannot be a component of itself.", nameof(componentId));
        if (!QuantityRules.IsValid(quantity))
            throw new ArgumentException("A component quantity breaks the quantity rule.", nameof(quantity));
        ArticleId = articleId;
        LineNo = lineNo;
        ComponentId = componentId;
        Quantity = quantity;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>The product: the article the bill belongs to.</summary>
    public Guid ArticleId { get; private set; }

    /// <summary>1…n, the position in the bill.</summary>
    public int LineNo { get; private set; }

    public Guid ComponentId { get; private set; }

    /// <summary>Of the component, in its base unit, per one base unit of the product.</summary>
    public decimal Quantity { get; private set; }

    public DateTime UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
}

/// <summary>Invariants of production (spec W01, section 3). The configurable rules are in the rule registry.</summary>
public static class ProductionRules
{
    /// <summary>The most components a bill or a work order may have, like the lines of a document.</summary>
    public const int MaxComponents = StockDocument.MaxLines;

    /// <summary>
    /// The quantity of a component a work order plans: per-unit quantity times the order's quantity, rounded to
    /// the quantity precision (half away from zero). May be 0 when the product is made in tiny amounts.
    /// </summary>
    public static decimal Planned(decimal perUnit, decimal quantity) =>
        decimal.Round(perUnit * quantity, QuantityRules.DecimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What a completion consumes of a line by default: its planned quantity scaled by produced / planned
    /// quantity of the order, rounded like <see cref="Planned"/>. Equal to the planned quantity when as much was
    /// produced as was planned.
    /// </summary>
    public static decimal Scaled(decimal planned, decimal orderQuantity, decimal producedQuantity) =>
        producedQuantity == orderQuantity
            ? planned
            : decimal.Round(planned * producedQuantity / orderQuantity, QuantityRules.DecimalPlaces, MidpointRounding.AwayFromZero);

    /// <summary>A consumed quantity: 0 (nothing of the component was used) or a valid quantity.</summary>
    public static bool IsValidConsumption(decimal quantity) => quantity == 0 || QuantityRules.IsValid(quantity);
}

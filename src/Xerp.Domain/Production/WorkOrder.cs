using Xerp.Domain.Common;
using Xerp.Domain.Inventory;

namespace Xerp.Domain.Production;

public enum WorkOrderStatus
{
    /// <summary>Freely editable and deletable; no number, no effect on stock.</summary>
    Draft,

    /// <summary>Numbered and handed to production; its header and lines no longer change. Can be completed or cancelled.</summary>
    Released,

    /// <summary>Production is booked: the components were issued and the product received, by two posted stock documents.</summary>
    Completed,

    /// <summary>Released and then called off before anything was produced. Final.</summary>
    Cancelled,

    /// <summary>Completed and then undone: both of its stock documents were reversed. Final.</summary>
    Reversed,
}

/// <summary>The contract names of <see cref="WorkOrderStatus"/>, as stored and as sent to clients.</summary>
public static class WorkOrderStatusNames
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Reversed = "reversed";

    public static readonly IReadOnlyList<string> All = [Draft, Released, Completed, Cancelled, Reversed];

    public static string ToName(this WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Draft => Draft,
        WorkOrderStatus.Released => Released,
        WorkOrderStatus.Completed => Completed,
        WorkOrderStatus.Cancelled => Cancelled,
        WorkOrderStatus.Reversed => Reversed,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static WorkOrderStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new ArgumentException($"Unknown work order status '{name}'.", nameof(name));

    public static bool TryParse(string? name, out WorkOrderStatus status)
    {
        status = name switch
        {
            Draft => WorkOrderStatus.Draft,
            Released => WorkOrderStatus.Released,
            Completed => WorkOrderStatus.Completed,
            Cancelled => WorkOrderStatus.Cancelled,
            Reversed => WorkOrderStatus.Reversed,
            _ => (WorkOrderStatus)(-1),
        };
        return (int)status >= 0;
    }
}

/// <summary>A component line as entered: an article and the quantity planned, in the article's base unit.</summary>
public readonly record struct WorkOrderLineEntry(Guid ArticleId, decimal PlannedQuantity);

/// <summary>
/// A work order (spec W01): make <see cref="Quantity"/> of one stock article in one warehouse from its
/// components. Prepared as a draft, released with a number, then completed: completion issues the components
/// and receives the product through two ordinary stock documents posted in the same transaction, so the stock
/// ledger stays the only record of stock. All quantities are in base units.
/// </summary>
public sealed class WorkOrder : ITenantOwned
{
    public const int NumberMaxLength = StockDocument.NumberMaxLength;

    private readonly List<WorkOrderLine> _lines = [];

    private WorkOrder() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    public WorkOrderStatus Status { get; private set; }

    /// <summary>Null on a draft; assigned at release (or at completion of a draft).</summary>
    public string? Number { get; private set; }

    /// <summary>The product: a stock article.</summary>
    public Guid ArticleId { get; private set; }

    /// <summary>How much of the product is to be made, in its base unit.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Where the components are taken from and the product goes to.</summary>
    public Guid WarehouseId { get; private set; }

    public DateOnly PlannedDate { get; private set; }
    public string? Reference { get; private set; }
    public string? Note { get; private set; }

    /// <summary>What was actually made; null until completed.</summary>
    public decimal? ProducedQuantity { get; private set; }

    /// <summary>The date of the two stock documents of the completion; null until completed.</summary>
    public DateOnly? CompletedDate { get; private set; }

    /// <summary>The issue of the components; null until completed, and when nothing was consumed.</summary>
    public Guid? IssueDocumentId { get; private set; }

    /// <summary>The receipt of the product; null until completed.</summary>
    public Guid? ReceiptDocumentId { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTime? ReleasedAt { get; private set; }
    public Guid? ReleasedBy { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public DateTime? ReversedAt { get; private set; }
    public Guid? ReversedBy { get; private set; }

    public IReadOnlyList<WorkOrderLine> Lines
    {
        get
        {
            _lines.Sort((a, b) => a.LineNo.CompareTo(b.LineNo));
            return _lines;
        }
    }

    public bool IsDraft => Status == WorkOrderStatus.Draft;

    /// <summary>Whether it can still be completed: a draft (released on the way) or a released order.</summary>
    public bool CanBeCompleted => Status is WorkOrderStatus.Draft or WorkOrderStatus.Released;

    public static WorkOrder Create(
        Guid articleId, decimal quantity, Guid warehouseId, DateOnly plannedDate, string? reference, string? note,
        IReadOnlyList<WorkOrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        var order = new WorkOrder
        {
            Id = Guid.CreateVersion7(),
            Status = WorkOrderStatus.Draft,
            CreatedAt = now,
            CreatedBy = actorKeyId,
        };
        order.Replace(articleId, quantity, warehouseId, plannedDate, reference, note, lines, now, actorKeyId);
        return order;
    }

    /// <summary>Replaces header and lines of a draft; returns the lines no longer on it, for the caller to delete.</summary>
    /// <exception cref="InvalidOperationException">The order is not a draft.</exception>
    public IReadOnlyList<WorkOrderLine> Replace(
        Guid articleId, decimal quantity, Guid warehouseId, DateOnly plannedDate, string? reference, string? note,
        IReadOnlyList<WorkOrderLineEntry> lines, DateTime now, Guid actorKeyId)
    {
        EnsureStatus(WorkOrderStatus.Draft, "changed");
        if (!QuantityRules.IsValid(quantity))
            throw new ArgumentException("The quantity breaks the quantity rule.", nameof(quantity));
        if (lines.Count is < 1 or > ProductionRules.MaxComponents)
            throw new ArgumentException($"A work order has 1 to {ProductionRules.MaxComponents} component lines.", nameof(lines));
        if (lines.Any(l => !QuantityRules.IsValid(l.PlannedQuantity)))
            throw new ArgumentException("A planned quantity breaks the quantity rule.", nameof(lines));
        if (lines.Any(l => l.ArticleId == articleId))
            throw new ArgumentException("The product cannot be its own component.", nameof(lines));
        var newReference = OptionalTextRules.Normalize(reference, StockDocument.ReferenceMaxLength, nameof(reference));
        if (!NoteRules.TryNormalize(note, StockDocument.NoteMaxLength, out var newNote))
            throw new ArgumentException("Invalid note.", nameof(note));

        ArticleId = articleId;
        Quantity = quantity;
        WarehouseId = warehouseId;
        PlannedDate = plannedDate;
        Reference = newReference;
        Note = newNote;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;

        var current = Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < current.Count)
                current[i].Set(lines[i]);
            else
                _lines.Add(new WorkOrderLine(Id, i + 1, lines[i]));
        }
        var removed = _lines.Skip(lines.Count).ToList();
        _lines.RemoveRange(lines.Count, removed.Count);
        return removed;
    }

    /// <summary>Gives the draft its number; header and lines are fixed from now on.</summary>
    public void Release(string number, DateTime now, Guid actorKeyId)
    {
        EnsureStatus(WorkOrderStatus.Draft, "released");
        if (string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("A released work order needs a number.", nameof(number));
        Status = WorkOrderStatus.Released;
        Number = number;
        ReleasedAt = now;
        ReleasedBy = actorKeyId;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    public void Cancel(DateTime now, Guid actorKeyId)
    {
        EnsureStatus(WorkOrderStatus.Released, "cancelled");
        Status = WorkOrderStatus.Cancelled;
        CancelledAt = now;
        CancelledBy = actorKeyId;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    /// <summary>
    /// Records the completion of a released order: what was produced and consumed, and the posted stock documents
    /// that moved it. <paramref name="consumed"/> has one quantity per line, in line order.
    /// </summary>
    public void Complete(
        decimal producedQuantity, IReadOnlyList<decimal> consumed, DateOnly completedDate, Guid? issueDocumentId, Guid receiptDocumentId,
        DateTime now, Guid actorKeyId)
    {
        EnsureStatus(WorkOrderStatus.Released, "completed");
        if (!QuantityRules.IsValid(producedQuantity))
            throw new ArgumentException("The produced quantity breaks the quantity rule.", nameof(producedQuantity));
        var lines = Lines;
        if (consumed.Count != lines.Count || consumed.Any(c => !ProductionRules.IsValidConsumption(c)))
            throw new ArgumentException("Completion needs a valid consumed quantity for every line.", nameof(consumed));
        if (issueDocumentId is null != consumed.All(c => c == 0))
            throw new ArgumentException("There is an issue document exactly when something was consumed.", nameof(issueDocumentId));
        for (var i = 0; i < lines.Count; i++)
            lines[i].Consume(consumed[i]);
        Status = WorkOrderStatus.Completed;
        ProducedQuantity = producedQuantity;
        CompletedDate = completedDate;
        IssueDocumentId = issueDocumentId;
        ReceiptDocumentId = receiptDocumentId;
        CompletedAt = now;
        CompletedBy = actorKeyId;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    /// <summary>Marks a completed order undone; the caller has reversed both of its stock documents in the same transaction.</summary>
    public void MarkReversed(DateTime now, Guid actorKeyId)
    {
        EnsureStatus(WorkOrderStatus.Completed, "reversed");
        Status = WorkOrderStatus.Reversed;
        ReversedAt = now;
        ReversedBy = actorKeyId;
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }

    private void EnsureStatus(WorkOrderStatus required, string attempted)
    {
        if (Status != required)
            throw new InvalidOperationException($"A {Status.ToName()} work order cannot be {attempted}.");
    }
}

/// <summary>One component of a work order: planned, and once completed, consumed - both in the component's base unit.</summary>
public sealed class WorkOrderLine : ITenantOwned
{
    private WorkOrderLine() { }

    internal WorkOrderLine(Guid workOrderId, int lineNo, WorkOrderLineEntry entry)
    {
        Id = Guid.CreateVersion7();
        WorkOrderId = workOrderId;
        LineNo = lineNo;
        Set(entry);
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid WorkOrderId { get; private set; }
    public int LineNo { get; private set; }
    public Guid ArticleId { get; private set; }
    public decimal PlannedQuantity { get; private set; }

    /// <summary>Null until the order is completed; 0 when none of the component was used.</summary>
    public decimal? ConsumedQuantity { get; private set; }

    public WorkOrderLineEntry Entry => new(ArticleId, PlannedQuantity);

    internal void Set(WorkOrderLineEntry entry)
    {
        ArticleId = entry.ArticleId;
        PlannedQuantity = entry.PlannedQuantity;
    }

    internal void Consume(decimal quantity) => ConsumedQuantity = quantity;
}

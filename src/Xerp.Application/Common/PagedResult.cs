namespace Xerp.Application.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Limit, int Offset);

/// <summary>A list that is not paged: <c>{ items }</c> (spec A01).</summary>
public sealed record ItemsResult<T>(IReadOnlyList<T> Items);

namespace Xerp.Application.Agent;

/// <summary>Spec A01, 2.2: one topic of the guide.</summary>
public sealed record GuideTopicDto(string Topic, string Summary);

public sealed record GuideDto(string Topic, string Summary, string Markdown);

public sealed record GuideInput(string? Topic = null);

/// <summary>Spec A01, 2.3: one line of the checklist.</summary>
public sealed record SetupItemDto(string Key, bool Done, string Detail, string NextStep);

public sealed record SetupStatusDto(bool Ready, int Done, int Total, IReadOnlyList<SetupItemDto> Items);

/// <summary>Spec A01, 2.5: a hit of <c>search</c>; <c>Code</c> or <c>Number</c> and <c>Name</c> or <c>Label</c> are set according to the type, the others are null. A draft has no number yet: its <c>Code</c> is then its reference, the label a person can quote.</summary>
public sealed record SearchHitDto(string Type, Guid Id, string? Code, string? Number, string? Name, string? Label, string? Status);

public sealed record SearchInput(string? Text = null, IReadOnlyList<string>? Types = null);

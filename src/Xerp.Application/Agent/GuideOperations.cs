using System.Reflection;
using Xerp.Application.Common;
using Xerp.Application.Rules;
using Xerp.Domain.Rules;

namespace Xerp.Application.Agent;

/// <summary>
/// Spec A01, 2.2: the playbooks. Markdown files in <c>Guide/</c>, embedded in this assembly, written for an agent and
/// reviewed like code. Line 1 is the title (<c># ...</c>), line 3 the one-line summary. They are the same for every tenant.
/// </summary>
public sealed class GuideOperations
{
    /// <summary>The order in which the topics are listed; a playbook not named here follows, in name order.</summary>
    private static readonly string[] Order =
    [
        "getting-started", "company-setup", "articles-and-prices", "partners", "buying", "selling", "invoicing", "stock",
        "production", "business-rules", "reports", "month-end", "errors",
    ];

    private static readonly Lazy<IReadOnlyList<GuideDto>> Playbooks = new(Load);

    /// <summary>All playbooks, in listing order (also used by the tests that check the tool names they mention).</summary>
    public static IReadOnlyList<GuideDto> All => Playbooks.Value;

    public Result<ItemsResult<GuideTopicDto>> List() =>
        new ItemsResult<GuideTopicDto>(All.Select(g => new GuideTopicDto(g.Topic, g.Summary)).ToList());

    public Result<GuideDto> Get(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return AppError.Validation("topic", "topic is required here; call guide without a topic for the list.");
        var playbook = All.FirstOrDefault(g => string.Equals(g.Topic, topic.Trim(), StringComparison.Ordinal));
        return playbook is null
            ? AppError.NotFound($"No guide topic '{topic}'. Topics: {string.Join(", ", All.Select(g => g.Topic))}.")
            : playbook;
    }

    /// <summary>The MCP tool: no topic lists the topics, a topic reads its playbook.</summary>
    public Result<object> Answer(string? topic)
    {
        if (topic is null)
            return (object)List().Value;
        var playbook = Get(topic);
        if (!playbook.IsSuccess)
            return playbook.Error;
        return (object)playbook.Value;
    }

    private static IReadOnlyList<GuideDto> Load()
    {
        var assembly = typeof(GuideOperations).Assembly;
        var playbooks = new List<GuideDto>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".md", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var markdown = reader.ReadToEnd().Replace("\r\n", "\n");
            var topic = resource[Prefix.Length..^3];
            var lines = markdown.Split('\n');
            var summary = lines.Skip(1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? topic;
            playbooks.Add(new GuideDto(topic, summary, markdown));
        }
        return playbooks.OrderBy(p => Array.IndexOf(Order, p.Topic) is var i and >= 0 ? i : int.MaxValue).ThenBy(p => p.Topic, StringComparer.Ordinal).ToList();
    }

    private const string Prefix = "Xerp.Application.Guide.";
}

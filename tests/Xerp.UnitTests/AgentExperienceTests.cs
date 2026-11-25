using System.Text.RegularExpressions;
using Xerp.Api.Mcp;
using Xerp.Application.Agent;
using Xerp.Domain.Rules;

namespace Xerp.UnitTests;

/// <summary>Spec A01: the profiles are data in code and every key is checked; the playbooks and the tool catalogue agree.</summary>
public class AgentExperienceTests
{
    [Fact]
    public void Every_key_of_every_profile_is_a_rule_of_the_registry_once_and_the_defaults_profile_holds_every_rule_at_its_default()
    {
        foreach (var profile in RuleProfiles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Description), profile.Key);
            Assert.NotEmpty(profile.Changes);
            Assert.Equal(profile.Changes.Count, profile.Changes.Select(c => c.Key).Distinct().Count());
            Assert.All(profile.Changes, c => Assert.NotNull(RuleRegistry.Find(c.Key)));
        }
        var defaults = Assert.IsType<RuleProfile>(RuleProfiles.Find(RuleProfiles.Defaults));
        Assert.Equal(RuleRegistry.All.Count, defaults.Changes.Count);
        Assert.All(defaults.Changes, c => Assert.Equal(RuleRegistry.Find(c.Key)!.Default, c.Value));
        foreach (var key in new[] { "strict", "retail", "production" })
            Assert.Contains(RuleProfiles.Find(key)!.Changes, c => c.Value != RuleRegistry.Find(c.Key)!.Default);
    }

    [Fact]
    public void Every_group_of_rules_has_a_description()
    {
        foreach (var group in RuleRegistry.All.Select(r => r.Group).Distinct())
            Assert.DoesNotContain("Business rules of the area", RuleGroups.Describe(group));
    }

    [Fact]
    public void Every_tool_is_mentioned_by_a_playbook_and_every_tool_a_playbook_mentions_exists()
    {
        var tools = ToolCatalog.All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var text = string.Join("\n", GuideOperations.All.Select(g => g.Markdown));
        var missing = tools.Where(t => !Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(t)}(?![A-Za-z0-9_])")).Order().ToArray();
        Assert.True(missing.Length == 0, $"No playbook mentions: {string.Join(", ", missing)}");

        // A backticked snake_case word that starts like a tool but is none: a typo or a tool that does not exist (yet).
        var prefixes = tools.Where(t => t.Contains((char)95)).Select(t => t[..t.IndexOf((char)95)]).ToHashSet();
        var unknown = Regex.Matches(text, @"`([a-z]+(?:_[a-z]+)+)[`( ]").Select(m => m.Groups[1].Value)
            .Where(w => prefixes.Contains(w[..w.IndexOf('_')]) && !tools.Contains(w)).Distinct().Order().ToArray();
        Assert.True(unknown.Length == 0, $"Playbooks mention tools that do not exist: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void The_playbooks_are_embedded_with_a_title_and_a_one_line_summary()
    {
        Assert.NotEmpty(GuideOperations.All);
        foreach (var g in GuideOperations.All)
        {
            Assert.StartsWith("# ", g.Markdown);
            Assert.False(string.IsNullOrWhiteSpace(g.Summary), g.Topic);
            Assert.DoesNotContain('\n', g.Summary);
        }
    }
}

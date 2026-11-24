using Xerp.Api.Http;
using Xerp.Application.Agent;
using Xerp.Application.Common;
using Xerp.Application.Invoicing;
using Xerp.Application.Rules;

namespace Xerp.Api.Endpoints;

/// <summary>Spec A01: the agent's way in - guide, setup status, rules in plain language, search. Thin: bind, call, map.</summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/guide", (GuideOperations operations) => Respond(operations.List()));
        v1.MapGet("/guide/{topic}", (string topic, GuideOperations operations) => Respond(operations.Get(topic)));

        v1.MapGet("/setup-status", async (SetupStatusOperations operations, CancellationToken ct) => Respond(await operations.GetAsync(ct)));
        v1.MapPost("/setup/rules-reviewed", async (CompanyOperations operations, CancellationToken ct) => Respond(await operations.MarkRulesReviewedAsync(ct)));

        v1.MapGet("/rule-groups", async (RuleOperations operations, CancellationToken ct) => Respond(await operations.ListGroupsAsync(ct)));
        v1.MapPost("/rules/set-many", async (HttpRequest request, RuleOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetManyRulesInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.SetManyAsync(body.Value, ct)) : Problems.From(body.Error);
        });
        v1.MapPost("/rules/preview", async (HttpRequest request, RuleOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<RulePreviewInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.PreviewAsync(body.Value, ct)) : Problems.From(body.Error);
        });
        v1.MapGet("/rule-profiles", (RuleProfileOperations operations) => Respond(operations.List()));
        v1.MapGet("/rule-profiles/{key}", (string key, RuleProfileOperations operations) => Respond(operations.Get(new RuleKeyInput(key))));

        v1.MapGet("/search", async (HttpRequest request, SearchOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var text = binder.Text("q");
            var types = binder.Text("types");
            if (binder.Error is { } error)
                return Problems.From(error);
            var input = new SearchInput(text, types?.Split(',', StringSplitOptions.TrimEntries));
            return Respond(await operations.SearchAsync(input, "q", ct));
        }).AcceptsQuery("q", "types");
    }

    private static IResult Respond<T>(Result<T> result) where T : notnull => result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
}

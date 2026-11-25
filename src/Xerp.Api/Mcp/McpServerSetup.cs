using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Pos;
using Xerp.Domain.Tenancy;
using System.Reflection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Xerp.Api.Mcp;

/// <summary>
/// The MCP server (ADR-0009): official SDK, stateless Streamable HTTP, tools only. Authentication and the
/// tenant are established by <c>ApiV1Middleware</c> for the HTTP request that carries the JSON-RPC message,
/// exactly as for <c>/api/v1</c>; nothing is kept between requests.
/// </summary>
public static class McpServerSetup
{
    public const string ServerName = "xerp";

    /// <summary>Static text: the same for every tenant (spec 003, T6).</summary>
    public const string Instructions =
        "Xerp is an ERP. Everything you read or write belongs to the company (tenant) of the API key that authenticates this connection; "
        + "there is no way to address another company. `whoami` shows the company and the actor your calls are attributed to.\n"
        + "\n"
        + "Start with three tools:\n"
        + "- `setup_status`: what this company still needs (company data, warehouses, tax rates, articles, partners, rules...), each open item with the tool to call.\n"
        + "- `guide`: playbooks for tasks, with the tools in order and the usual errors. Call it without a topic for the list; read `getting-started` first.\n"
        + "- `report_list`: the report catalogue (sales, VAT, unpaid invoices, stock, orders, production); run one with `report_run`.\n"
        + "\n"
        + "Workflow: setup -> masters (articles, partners) -> purchasing -> stock -> sales -> invoicing -> payments -> reports. "
        + "Find a record by a name, code, number or barcode with `search`; use the `id` it returns. Records are referenced by `id` (a UUID); a `code` is a label for lookup and can change.\n"
        + "\n"
        + "Business rules are the company's policy: yes/no switches with a shipped default (`rule_group_list`, `rule_list`, `rule_profile_list`). "
        + "Never change a rule unless the user asked for the policy to change, and never to get one refused call through; show the difference with `rule_preview` first and apply with `rule_set_many`.\n"
        + "\n"
        + "A failed call returns a tool error whose text is JSON `{ \"code\", \"detail\", \"errors\"?, \"rules\"? }`: branch on `code` (stable), read `detail`, and use the keys of `errors` to see which arguments to correct. "
        + "When `rules` is present, a business rule this company can switch refused the call: it names the rule's `key` and the `value` it has; when it is absent, no switch lifts the refusal. "
        + "Field names, rules and results are identical to the HTTP API.";

    private static readonly Dictionary<string, XerpTool> Tools = ToolCatalog.All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static readonly ListToolsResult ToolList = new() { Tools = ToolCatalog.All.Select(t => t.ToProtocolTool()).ToList() };

    /// <summary>Spec P02, R33: what a key with scope <c>pos</c> sees - exactly the tools of the allow-list.</summary>
    private static readonly ListToolsResult PosToolList = new()
    {
        Tools = ToolCatalog.All.Where(t => PosAllowList.AllowsTool(t.Name)).Select(t => t.ToProtocolTool()).ToList(),
    };

    public static IServiceCollection AddXerpMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = ServerName, Version = ApiVersion };
                options.ServerInstructions = Instructions;
                options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = false } };
            })
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithListToolsHandler((request, _) => ValueTask.FromResult(IsPosKey(request.Services) ? PosToolList : ToolList))
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    private static bool IsPosKey(IServiceProvider? services) =>
        services?.GetService<RequestActor>()?.Scope == ApiKeyScope.Pos;

    private static string ApiVersion =>
        typeof(McpServerSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var name = request.Params?.Name;
        // Not a tool result: the caller named something that does not exist (spec 003, E6).
        if (name is null || !Tools.TryGetValue(name, out var tool))
            throw new McpProtocolException($"Unknown tool: '{name}'.", McpErrorCode.InvalidParams);

        var services = request.Services ?? throw new InvalidOperationException("The MCP request has no service provider.");
        if (IsPosKey(services) && !PosAllowList.AllowsTool(name))
            return ToolResults.Error(AppError.Forbidden(
                "This API key is limited to POS work (scope pos): it may not call this tool. Use a key with scope full."));
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpServerSetup));
        try
        {
            var resultOptions = services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            return await tool.Invoke(services, request.Params!.Arguments, resultOptions, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception in MCP tool {Tool}", name);
            return ToolResults.Unexpected(ex);
        }
    }
}

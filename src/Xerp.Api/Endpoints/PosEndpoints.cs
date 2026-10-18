using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Pos;
using Xerp.Application.Sales;
using Xerp.Domain.Pos;

namespace Xerp.Api.Endpoints;

/// <summary>
/// Spec P02, section 4: POS roles and operators, operator sessions and authorize, all under <c>/pos</c>.
/// Thin: bind, call, map.
/// </summary>
public static class PosEndpoints
{
    public const string RoleRoute = "/pos/roles";
    public const string OperatorRoute = "/pos/operators";
    public const string SessionRoute = "/pos/sessions";
    public const string AuthorizeRoute = "/pos/authorize";

    public static void MapPosEndpoints(this IEndpointRouteBuilder v1)
    {
        SalesMasterDataEndpoints.MapCodedMaster<PosRoleOperations, PosRole, PosRoleDto, PosRoleInput, PosRoleValues, ListCodedMastersInput>(
            v1, RoleRoute, r => r.Id, [], SalesMasterDataEndpoints.BindList, (operations, input, ct) => operations.ListAsync(input, ct));
        MapOperators(v1);
        MapSessions(v1);
    }

    private static void MapOperators(IEndpointRouteBuilder v1)
    {
        var operators = v1.MapGroup(OperatorRoute);

        operators.MapGet("", async (HttpRequest request, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListPosOperatorsInput(binder.Text("search"), binder.Bool("isActive"), binder.Text("roleId"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isActive", "roleId", "limit", "offset");

        operators.MapGet("/{id:guid}", async (Guid id, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        operators.MapGet("/by-code/{code}", async (string code, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        operators.MapPost("", async (HttpRequest request, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreatePosOperatorInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{OperatorRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        operators.MapPut("/{id:guid}", async (Guid id, HttpRequest request, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<ReplacePosOperatorInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        operators.MapDelete("/{id:guid}", async (Guid id, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        operators.MapPut("/{id:guid}/pin", async (Guid id, HttpRequest request, PosOperatorOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetPosOperatorPinInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.SetPinAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }

    private static void MapSessions(IEndpointRouteBuilder v1)
    {
        var sessions = v1.MapGroup(SessionRoute);

        sessions.MapPost("", async (HttpRequest request, PosSessionOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<StartPosSessionInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.StartAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{SessionRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        sessions.MapGet("", async (HttpRequest request, PosSessionOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListPosSessionsInput(binder.Text("status"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("status", "limit", "offset");

        sessions.MapGet("/{id:guid}", async (Guid id, PosSessionOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        sessions.MapPost("/{id:guid}/end", async (Guid id, PosSessionOperations operations, CancellationToken ct) =>
        {
            var result = await operations.EndAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        v1.MapPost(AuthorizeRoute, async (HttpRequest request, PosSessionOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<AuthorizeInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.AuthorizeAsync(body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}

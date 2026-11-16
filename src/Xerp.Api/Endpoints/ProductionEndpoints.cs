using Xerp.Api.Http;
using Xerp.Application.Production;

namespace Xerp.Api.Endpoints;

/// <summary>Spec W01, section 4: bills of materials and work orders.</summary>
public static class ProductionEndpoints
{
    public const string WorkOrdersRoute = "/work-orders";

    public static void MapProductionEndpoints(this IEndpointRouteBuilder v1)
    {
        // ---- bill of materials: a sub-resource of the article
        var bom = v1.MapGroup("/articles/{articleId:guid}/bom");

        bom.MapGet("", async (Guid articleId, BomOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(articleId, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        bom.MapPut("", async (Guid articleId, HttpRequest request, BomOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetBomInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.SetAsync(articleId, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        bom.MapDelete("", async (Guid articleId, BomOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(articleId, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        // ---- work orders
        var orders = v1.MapGroup(WorkOrdersRoute);

        orders.MapGet("", async (HttpRequest request, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListWorkOrdersInput(
                binder.Text("status"), binder.Text("articleId"), binder.Text("warehouseId"), binder.Text("search"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("status", "articleId", "warehouseId", "search", "limit", "offset");

        orders.MapGet("/{id:guid}", async (Guid id, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapGet("/by-number/{number}", async (string number, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.FindAsync(new WorkOrderAddressInput(Number: number), ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("", async (HttpRequest request, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<WorkOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{WorkOrdersRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        orders.MapPut("/{id:guid}", async (Guid id, HttpRequest request, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<WorkOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapDelete("/{id:guid}", async (Guid id, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/release", async (Guid id, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.ReleaseAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/cancel", async (Guid id, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var result = await operations.CancelAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/complete", async (Guid id, HttpRequest request, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadOrEmptyAsync<CompleteWorkOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CompleteAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        orders.MapPost("/{id:guid}/reverse", async (Guid id, HttpRequest request, WorkOrderOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadOrEmptyAsync<ReverseWorkOrderInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReverseAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }
}

using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Invoicing;

namespace Xerp.Api.Endpoints;

/// <summary>Spec I01: invoices, their payments, and the company profile.</summary>
public static class InvoiceEndpoints
{
    public const string Route = "/invoices";

    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder v1)
    {
        var invoices = v1.MapGroup(Route);

        invoices.MapGet("", async (HttpRequest request, InvoiceOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListInvoicesInput(binder.Text("status"), binder.Text("paymentStatus"), binder.Text("customerId"),
                binder.Text("search"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            return Respond(await operations.ListAsync(input, ct));
        }).AcceptsQuery("status", "paymentStatus", "customerId", "search", "limit", "offset");

        invoices.MapGet("/{id:guid}", async (Guid id, InvoiceOperations operations, CancellationToken ct) => Respond(await operations.GetAsync(id, ct)));

        invoices.MapGet("/by-number/{number}", async (string number, InvoiceOperations operations, CancellationToken ct) =>
            Respond(await operations.FindAsync(new InvoiceAddressInput(Number: number), ct)));

        invoices.MapPost("", async (HttpRequest request, InvoiceOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<InvoiceInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value) : Problems.From(result.Error);
        });

        invoices.MapPut("/{id:guid}", async (Guid id, HttpRequest request, InvoiceOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<InvoiceInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.ReplaceAsync(id, body.Value, ct)) : Problems.From(body.Error);
        });

        invoices.MapDelete("/{id:guid}", async (Guid id, InvoiceOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });

        invoices.MapPost("/{id:guid}/issue", async (Guid id, InvoiceOperations operations, CancellationToken ct) =>
            Respond(await operations.IssueAsync(id, ct)));

        invoices.MapPost("/{id:guid}/cancel", async (Guid id, HttpRequest request, InvoiceOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadOrEmptyAsync<CancelInvoiceInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.CancelAsync(id, body.Value, ct)) : Problems.From(body.Error);
        });

        invoices.MapPost("/{id:guid}/payments", async (Guid id, HttpRequest request, InvoiceOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<InvoicePaymentInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.AddPaymentAsync(id, body.Value, ct)) : Problems.From(body.Error);
        });

        invoices.MapDelete("/{id:guid}/payments/{paymentId:guid}", async (Guid id, Guid paymentId, InvoiceOperations operations, CancellationToken ct) =>
            Respond(await operations.DeletePaymentAsync(id, paymentId, ct)));

        var company = v1.MapGroup("/company");
        company.MapGet("", async (CompanyOperations operations, CancellationToken ct) => Respond(await operations.GetAsync(ct)));
        company.MapPut("", async (HttpRequest request, CompanyOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CompanyProfileInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.SetAsync(body.Value, ct)) : Problems.From(body.Error);
        });
    }

    private static IResult Respond<T>(Result<T> result) where T : notnull => result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
}

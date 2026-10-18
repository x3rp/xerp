using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Sales;
using Xerp.Domain.Sales;

namespace Xerp.Api.Endpoints;

/// <summary>
/// The sales master data of spec P01, section 4: three masters with the six operations of a unit of measure,
/// the sales data of an article and the barcodes of articles. Thin: bind, call, map.
/// </summary>
public static class SalesMasterDataEndpoints
{
    public const string ArticleCategoryRoute = "/article-categories";
    public const string TaxRateRoute = "/tax-rates";
    public const string PaymentMethodRoute = "/payment-methods";
    public const string ArticleBarcodeRoute = "/article-barcodes";
    public const string SalesDataSegment = "/sales-data";

    public static void MapSalesMasterDataEndpoints(this IEndpointRouteBuilder v1)
    {
        MapCodedMaster<ArticleCategoryOperations, ArticleCategory, ArticleCategoryDto, ArticleCategoryInput, CodedMasterValues, ListCodedMastersInput>(
            v1, ArticleCategoryRoute, c => c.Id, [], BindList, (operations, input, ct) => operations.ListAsync(input, ct));
        MapCodedMaster<TaxRateOperations, TaxRate, TaxRateDto, TaxRateInput, TaxRateValues, ListCodedMastersInput>(
            v1, TaxRateRoute, t => t.Id, [], BindList, (operations, input, ct) => operations.ListAsync(input, ct));
        MapCodedMaster<PaymentMethodOperations, PaymentMethod, PaymentMethodDto, PaymentMethodInput, PaymentMethodValues, ListPaymentMethodsInput>(
            v1, PaymentMethodRoute, m => m.Id, ["kind"],
            binder => new ListPaymentMethodsInput(binder.Text("search"), binder.Text("kind"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset")),
            (operations, input, ct) => operations.ListAsync(input, ct));
        MapSalesData(v1);
        MapBarcodes(v1);
    }

    internal static ListCodedMastersInput BindList(QueryBinder binder) =>
        new(binder.Text("search"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));

    /// <summary>The six operations of <c>/units-of-measure</c> (spec 001, 4.3) for one sales master.</summary>
    internal static void MapCodedMaster<TOperations, TEntity, TDto, TInput, TValues, TList>(
        IEndpointRouteBuilder v1, string route, Func<TDto, Guid> idOf, string[] ownFilters,
        Func<QueryBinder, TList> bindList,
        Func<TOperations, TList, CancellationToken, Task<Result<PagedResult<TDto>>>> list)
        where TOperations : CodedMasterOperations<TEntity, TDto, TInput, TValues>
        where TEntity : CodedMaster
        where TDto : notnull
        where TInput : class
        where TValues : CodedMasterValues
    {
        var records = v1.MapGroup(route);

        records.MapGet("", async (HttpRequest request, TOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = bindList(binder);
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await list(operations, input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery(["search", "isActive", "limit", "offset", .. ownFilters]);

        // A malformed id does not match the route and ends as NOT_FOUND like any unknown path.
        records.MapGet("/{id:guid}", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        records.MapGet("/by-code/{code}", async (string code, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetByCodeAsync(code, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        records.MapPost("", async (HttpRequest request, TOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<TInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{route}/{idOf(result.Value)}", result.Value)
                : Problems.From(result.Error);
        });

        records.MapPut("/{id:guid}", async (Guid id, HttpRequest request, TOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<TInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.ReplaceAsync(id, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        records.MapDelete("/{id:guid}", async (Guid id, TOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }

    /// <summary>A sub-resource of the article, as its conversions are; <c>/articles/sales-data</c> is a fixed path, not an article id.</summary>
    private static void MapSalesData(IEndpointRouteBuilder v1)
    {
        v1.MapGet(ArticleEndpoints.Route + SalesDataSegment, async (HttpRequest request, ArticleSalesDataOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListArticleSalesDataInput(
                binder.Text("search"), binder.Text("categoryId"), binder.Text("taxRateId"), binder.Bool("priced"), binder.Bool("isActive"),
                binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "categoryId", "taxRateId", "priced", "isActive", "limit", "offset");

        var salesData = v1.MapGroup(ArticleEndpoints.Route + "/{articleId:guid}" + SalesDataSegment);

        salesData.MapGet("", async (Guid articleId, ArticleSalesDataOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(articleId, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        salesData.MapPut("", async (Guid articleId, HttpRequest request, ArticleSalesDataOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetArticleSalesDataInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.SetAsync(articleId, body.Value, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });
    }

    private static void MapBarcodes(IEndpointRouteBuilder v1)
    {
        var barcodes = v1.MapGroup(ArticleBarcodeRoute);

        barcodes.MapGet("", async (HttpRequest request, ArticleBarcodeOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListArticleBarcodesInput(binder.Text("barcode"), binder.Text("articleId"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("barcode", "articleId", "limit", "offset");

        barcodes.MapGet("/{id:guid}", async (Guid id, ArticleBarcodeOperations operations, CancellationToken ct) =>
        {
            var result = await operations.GetAsync(id, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        barcodes.MapPost("", async (HttpRequest request, ArticleBarcodeOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateArticleBarcodeInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1{ArticleBarcodeRoute}/{result.Value.Id}", result.Value)
                : Problems.From(result.Error);
        });

        barcodes.MapDelete("/{id:guid}", async (Guid id, ArticleBarcodeOperations operations, CancellationToken ct) =>
        {
            var result = await operations.DeleteAsync(id, ct);
            return result.IsSuccess ? Results.NoContent() : Problems.From(result.Error);
        });
    }
}

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Xerp.Api.Endpoints;
using Xerp.Api.Http;
using Xerp.Api.Mcp;
using Xerp.Application.ApiKeys;
using Xerp.Application.Articles;
using Xerp.Application.ArticleUnits;
using Xerp.Application.Identity;
using Xerp.Application.Orders;
using Xerp.Application.Partners;
using Xerp.Application.Ports;
using Xerp.Application.Pos;
using Xerp.Application.Production;
using Xerp.Application.Rules;
using Xerp.Application.Sales;
using Xerp.Application.Stock;
using Xerp.Application.Tenants;
using Xerp.Application.UnitsOfMeasure;
using Xerp.Application.Warehouses;
using Xerp.Infrastructure;
using Xerp.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Composition root. Settings are read when first needed, not here, so that a host built by tests
// (WebApplicationFactory) sees the test configuration.
builder.Services.AddXerpInfrastructure(provider =>
    provider.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured."));
builder.Services.AddSingleton(provider => new AdminKeySetting(provider.GetRequiredService<IConfiguration>()["Xerp:AdminKey"]));
builder.Services.AddScoped<RequestActor>();
builder.Services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestActor>());
builder.Services.AddScoped<CredentialResolver>();
builder.Services.AddScoped<WhoAmIOperation>();
builder.Services.AddScoped<TenantProvisioning>();
builder.Services.AddScoped<UnitOfMeasureOperations>();
builder.Services.AddScoped<ArticleOperations>();
builder.Services.AddScoped<ArticleUnitOperations>();
builder.Services.AddScoped<PartnerOperations>();
builder.Services.AddScoped<WarehouseOperations>();
builder.Services.AddScoped<StockDocumentOperations>();
builder.Services.AddScoped<StockQueries>();
builder.Services.AddScoped<StockBalances>();
builder.Services.AddScoped<PurchaseOrderOperations>();
builder.Services.AddScoped<SalesOrderOperations>();
builder.Services.AddScoped<ApiKeyOperations>();
builder.Services.AddScoped<RuleOperations>();
builder.Services.AddScoped<ArticleCategoryOperations>();
builder.Services.AddScoped<TaxRateOperations>();
builder.Services.AddScoped<PaymentMethodOperations>();
builder.Services.AddScoped<ArticleSalesDataOperations>();
builder.Services.AddScoped<ArticleBarcodeOperations>();
builder.Services.AddScoped<PosRoleOperations>();
builder.Services.AddScoped<PosOperatorOperations>();
builder.Services.AddScoped<PosSignIn>();
builder.Services.AddScoped<PosSessionOperations>();
builder.Services.AddScoped<BomOperations>();
builder.Services.AddScoped<WorkOrderOperations>();
builder.Services.AddXerpMcpServer();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<XerpDbContext>().Database.MigrateAsync();

if (!app.Services.GetRequiredService<AdminKeySetting>().IsConfigured)
    app.Logger.LogWarning(
        "Xerp:AdminKey is not configured (or is shorter than {MinLength} characters): admin routes reject every request.",
        AdminKey.MinLength);

// Behind a reverse proxy (compose.prod.yaml) the client address and scheme come from X-Forwarded-*.
// Only switch this on when the API is reachable through the proxy alone: any client could forge the headers otherwise.
if (app.Configuration.GetValue<bool>("Xerp:ForwardedHeaders"))
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    };
    // The proxy's address on the container network is not fixed; trust is given by the network layout instead.
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();
app.UseMiddleware<ApiV1Middleware>();

app.MapGet("/health", async (XerpDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ok", db = "ok" })
        : Results.Json(new { status = "degraded", db = "down" }, statusCode: StatusCodes.Status503ServiceUnavailable));

var v1 = app.MapGroup("/api/v1").RejectUndefinedQueryParameters();
v1.MapTenantEndpoints();
v1.MapUnitOfMeasureEndpoints();
v1.MapArticleEndpoints();
v1.MapArticleUnitEndpoints();
v1.MapPartnerEndpoints();
v1.MapWarehouseEndpoints();
v1.MapStockEndpoints();
v1.MapPurchaseOrderEndpoints();
v1.MapSalesOrderEndpoints();
v1.MapApiKeyEndpoints();
v1.MapRuleEndpoints();
v1.MapSalesMasterDataEndpoints();
v1.MapPosEndpoints();
v1.MapProductionEndpoints();

// One operation = one HTTP endpoint + one MCP tool; both call the same Application operations.
app.MapMcp(ApiV1Middleware.McpPath);

app.Run();

public partial class Program;

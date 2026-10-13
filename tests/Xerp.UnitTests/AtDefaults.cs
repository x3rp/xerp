using Xerp.Application.Articles;
using Xerp.Application.Common;
using Xerp.Application.Partners;
using Xerp.Application.Ports;
using Xerp.Application.Warehouses;

namespace Xerp.UnitTests;

/// <summary>
/// The validations of the masters as a tenant that has set no rule sees them (spec 013: every default is the
/// behaviour of specs 001-012), so the tests of those specs read as they did.
/// </summary>
internal static class AtDefaults
{
    public static Result<ArticleValues> Create(CreateArticleInput input) => ArticleValidation.Create(input, TenantRules.Defaults);
    public static Result<ArticleValues> Replace(ReplaceArticleInput input) => ArticleValidation.Replace(input, TenantRules.Defaults);
    public static Result<PartnerValues> Create(PartnerInput input) => PartnerValidation.Create(input, TenantRules.Defaults);
    public static Result<PartnerValues> Replace(PartnerInput input) => PartnerValidation.Replace(input, TenantRules.Defaults);
    public static Result<WarehouseValues> Create(WarehouseInput input) => WarehouseValidation.Create(input, TenantRules.Defaults);
    public static Result<WarehouseValues> Replace(WarehouseInput input) => WarehouseValidation.Replace(input, TenantRules.Defaults);
}

using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Catalog;
using Xerp.Domain.Rules;

namespace Xerp.Application.Articles;

/// <summary>
/// Input rules for articles (spec 002, R1-R5, R10, R11, R18, R19; spec 013, R5). No I/O: the tenant's rules
/// are an argument, and a tenant that has set none is validated exactly as before spec 013.
/// </summary>
public static class ArticleValidation
{
    public static Result<ArticleValues> Create(CreateArticleInput input, TenantRules rules)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        var description = Description(errors, input.Description, rules);
        var type = RequiredType(errors, input.Type);
        var baseUnitId = RequiredBaseUnitId(errors, input.BaseUnitId);
        if (errors.Any)
            return errors.ToError();
        return new ArticleValues(code, name, description, type, baseUnitId, input.IsActive ?? true);
    }

    public static Result<ArticleValues> Replace(ReplaceArticleInput input, TenantRules rules)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        if (!input.DescriptionProvided)
            errors.Add("description", "description is required (it may be null).");
        var description = Description(errors, input.Description, rules);
        var type = RequiredType(errors, input.Type);
        var baseUnitId = RequiredBaseUnitId(errors, input.BaseUnitId);
        if (input.IsActive is null)
            errors.Add("isActive", "isActive is required.");
        if (errors.Any)
            return errors.ToError();
        return new ArticleValues(code, name, description, type, baseUnitId, input.IsActive!.Value);
    }

    public static Result<ArticleListQuery> List(ListArticlesInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);

        ArticleType? type = null;
        if (!string.IsNullOrEmpty(input.Type))
        {
            if (ArticleTypeNames.TryParse(input.Type, out var parsed)) type = parsed;
            else errors.Add("type", TypeMessage);
        }
        Guid? baseUnitId = null;
        if (!string.IsNullOrEmpty(input.BaseUnitId))
        {
            if (Guid.TryParse(input.BaseUnitId, out var parsed)) baseUnitId = parsed;
            else errors.Add("baseUnitId", "baseUnitId must be a UUID.");
        }
        Guid? alternativeUnitId = null;
        if (!string.IsNullOrEmpty(input.AlternativeUnitId))
        {
            if (Guid.TryParse(input.AlternativeUnitId, out var parsed)) alternativeUnitId = parsed;
            else errors.Add("alternativeUnitId", "alternativeUnitId must be a UUID.");
        }

        if (errors.Any)
            return errors.ToError();
        return new ArticleListQuery(search, type, baseUnitId, input.IsActive, limit, offset, alternativeUnitId);
    }

    private const string TypeMessage = $"type must be \"{ArticleTypeNames.Stock}\" or \"{ArticleTypeNames.Service}\".";

    /// <summary>The form of a description that is given is an invariant; whether one must be given is the rule article.descriptionRequired.</summary>
    private static string? Description(ValidationErrors errors, string? input, TenantRules rules)
    {
        if (!ArticleRules.TryNormalizeDescription(input, out var description))
            errors.Add("description", $"description must be at most {ArticleRules.DescriptionMaxLength} characters and contain no control characters other than line breaks and tabs.");
        RequiredFields.Of(rules, RuleRegistry.Article).Check(errors, "description", description);
        return description;
    }

    private static ArticleType RequiredType(ValidationErrors errors, string? input)
    {
        if (!ArticleTypeNames.TryParse(input, out var type))
            errors.Add("type", string.IsNullOrEmpty(input) ? "type is required. " + TypeMessage : TypeMessage);
        return type;
    }

    /// <summary>Only the form is checked here; whether the unit exists is a later stage (R13).</summary>
    private static Guid RequiredBaseUnitId(ValidationErrors errors, string? input)
    {
        if (Guid.TryParse(input, out var id))
            return id;
        errors.Add("baseUnitId", string.IsNullOrEmpty(input)
            ? "baseUnitId is required."
            : "baseUnitId must be the id (UUID) of a unit of measure, not its code.");
        return default;
    }
}

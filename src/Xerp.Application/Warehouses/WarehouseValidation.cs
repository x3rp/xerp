using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Rules;

namespace Xerp.Application.Warehouses;

/// <summary>
/// Input rules for warehouses (spec 004, R1-R9, R13; spec 013, R5). No I/O: the tenant's rules are an argument,
/// and a tenant that has set none is validated exactly as before spec 013.
/// </summary>
public static class WarehouseValidation
{
    /// <summary><c>isActive</c> defaults to true; the address fields may be omitted.</summary>
    public static Result<WarehouseValues> Create(WarehouseInput input, TenantRules rules) => Validate(input, rules, replace: false);

    /// <summary>All nine fields must be present; the address fields may be null (R7).</summary>
    public static Result<WarehouseValues> Replace(WarehouseInput input, TenantRules rules) => Validate(input, rules, replace: true);

    private static Result<WarehouseValues> Validate(WarehouseInput input, TenantRules rules, bool replace)
    {
        var errors = new ValidationErrors();
        var code = errors.Code(input.Code);
        var name = errors.Name(input.Name);
        var address = FieldRules.AddressOf(errors, input, replace, RequiredFields.Of(rules, RuleRegistry.Warehouse));
        var isActive = FieldRules.Flag(errors, input, nameof(input.IsActive), input.IsActive, true, replace);
        if (errors.Any)
            return errors.ToError();
        return new WarehouseValues(code, name, address, isActive);
    }

    public static Result<WarehouseListQuery> List(ListWarehousesInput input)
    {
        var errors = new ValidationErrors();
        var search = ListRules.Search(errors, input.Search);
        var limit = ListRules.Limit(errors, input.Limit);
        var offset = ListRules.Offset(errors, input.Offset);
        if (errors.Any)
            return errors.ToError();
        return new WarehouseListQuery(search, input.IsActive, limit, offset, input.IsDefault);
    }
}

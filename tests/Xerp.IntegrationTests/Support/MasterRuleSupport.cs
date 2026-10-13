using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xerp.IntegrationTests.Support;

/// <summary>
/// The 24 rules of spec 013 (section 6.1) and the conventions of its section 10: "minimal" records, "refused by
/// (CODE, key, v)" for the three kinds of refusal, and the default behaviour of AC-20.
/// </summary>
public static class MasterRules
{
    public const string RoleRequired = "partner.roleRequired";
    public const string TaxIdRequired = "partner.taxIdRequired";
    public const string TaxIdUnique = "partner.taxIdUnique";
    public const string DescriptionRequired = "article.descriptionRequired";
    public const string PartnerCityRequired = "partner.cityRequired";
    public const string WarehouseCityRequired = "warehouse.cityRequired";

    /// <summary>The first segment of the keys: the four masters.</summary>
    public static readonly string[] Groups = ["uom", "article", "partner", "warehouse"];

    /// <summary>The fourteen required-field rules as a table: (resource, field) -> <c>{group}.{field}Required</c>.</summary>
    public static readonly (string Group, string Field)[] RequiredFields =
    [
        ("article", "description"),
        ("partner", "taxId"),
        .. MasterApi.AddressFields.Select(f => ("partner", f)),
        .. MasterApi.AddressFields.Select(f => ("warehouse", f)),
    ];

    /// <summary>The 24 keys of spec 013.</summary>
    public static readonly string[] Keys =
    [
        .. Groups.Select(CodeChangeable),
        .. Groups.Select(NameUnique),
        .. RequiredFields.Select(r => Required(r.Group, r.Field)),
        RoleRequired,
        TaxIdUnique,
    ];

    public static string CodeChangeable(string group) => $"{group}.codeChangeable";

    public static string NameUnique(string group) => $"{group}.nameUnique";

    public static string Required(string group, string field) => $"{group}.{field}Required";

    /// <summary>A value of the field that every invariant accepts.</summary>
    public static string ValidValue(string field) => field switch
    {
        "description" => "A described article",
        "taxId" => "HR12345678901",
        "addressLine1" => "Ilica 1",
        "addressLine2" => "2nd floor",
        "postalCode" => "10000",
        "city" => "Zagreb",
        "region" => "Grad Zagreb",
        "countryCode" => "HR",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    public static IEnumerable<object[]> GroupData() => Groups.Select(g => new object[] { g });

    public static IEnumerable<object[]> KeyData() => Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> RequiredData() => RequiredFields.Select(r => new object[] { r.Group, r.Field });

    /// <summary>Sets every one of the 24 rules to the value that is not its default.</summary>
    public static async Task SetAllToOtherAsync(HttpClient client)
    {
        foreach (var key in Keys)
            await Rules.SetAsync(client, key, !Rules.Default(key));
    }

    // ---- "refused by" ----

    /// <summary><c>400 VALIDATION_FAILED</c> with exactly the field's key, refused by (<paramref name="rule"/>, <c>true</c>) (R5, R17).</summary>
    public static async Task<JsonElement> RequiredAsync(Task<HttpResponseMessage> send, string rule, string field)
    {
        using var response = await send;
        return await Rules.RefusedAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED", rule, true, field);
    }

    /// <summary>A <c>409</c> with exactly the given key, refused by (<paramref name="rule"/>, <paramref name="value"/>) (R9, R12, R15).</summary>
    public static async Task<JsonElement> ConflictAsync(Task<HttpResponseMessage> send, string code, string rule, bool value, string field)
    {
        using var response = await send;
        return await Rules.ConflictAsync(response, code, rule, value, field);
    }

    /// <summary><c>400</c> for a partner with neither role, refused by (<c>partner.roleRequired</c>, <c>true</c>), fields in that order (R8).</summary>
    public static async Task<JsonElement> RoleRequiredAsync(Task<HttpResponseMessage> send)
    {
        using var response = await send;
        var problem = await Rules.RefusedAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED", RoleRequired, true, "isCustomer", "isSupplier");
        Assert.Equal(new[] { "isCustomer", "isSupplier" }, Rules.Fields(Rules.AssertRule(problem, RoleRequired, true)));
        return problem;
    }

    /// <summary><c>400 VALIDATION_FAILED</c> with exactly the given key that no rule caused: no <c>rules</c> member (R6, R18).</summary>
    public static async Task<JsonElement> InvariantAsync(Task<HttpResponseMessage> send, string key)
    {
        using var response = await send;
        var problem = await HttpAssert.ValidationAsync(response, key);
        Assert.Equal(new[] { key }, McpAssert.ErrorKeys(problem));
        return Rules.AssertNamesNoRule(problem);
    }

    // ---- the defaults (AC-20, AC-21, AC-22, AC-90) ----

    /// <summary>
    /// AC-20: in this tenant the four masters behave as specs 001–012 say — a code may be changed (a change of
    /// case only included), names and tax ids need not be unique, the optional fields are optional, and a partner
    /// with neither role is refused. With <paramref name="named"/> (AC-22) that refusal names its rule. The tenant
    /// must not have records with the codes <c>DF…</c>.
    /// </summary>
    public static async Task AssertDefaultBehaviourAsync(HttpClient http, bool named)
    {
        foreach (var group in Groups)
        {
            var m = await RuledMaster.OpenAsync(group, http);
            var first = await m.CreateAsync(http, m.Minimal("DF1", "Same name"));

            // A replace may change the code (001/R6, 002/R12, 004/R8), a change of case only included.
            var changed = await m.ReplaceAsync(http, first.Id(), m.BodyOf(first).With("code", "DF2"));
            Assert.Equal("DF2", changed.Str("code"), ignoreCase: true);
            using (var byCode = await m.ByCodeAsync(http, "DF2"))
                Assert.Equal(first.Id(), (await HttpAssert.JsonAsync(byCode, HttpStatusCode.OK)).Id());
            using (var caseOnly = await m.PutAsync(http, first.Id(), m.BodyOf(changed).With("code", "df2")))
                await HttpAssert.JsonAsync(caseOnly, HttpStatusCode.OK);

            // Two records of the resource with the same name.
            var second = await m.CreateAsync(http, m.Minimal("DF3", "Same name"));
            Assert.Equal("Same name", second.Str("name"));

            // The optional fields are optional.
            if (group == "article")
                JsonBody.AssertNull(first, "description");
            if (group == "partner")
                JsonBody.AssertNull(first, ["taxId", .. MasterApi.AddressFields]);
            if (group == "warehouse")
                JsonBody.AssertNull(first, MasterApi.AddressFields);
        }

        // Two partners with the same tax id (004/R16).
        var p = await RuledMaster.OpenAsync("partner", http);
        await p.CreateAsync(http, p.Minimal("DF4", "Tax one").With("taxId", "HR999"));
        await p.CreateAsync(http, p.Minimal("DF5", "Tax two").With("taxId", "HR999"));

        // A partner with neither role (004/R15).
        var before = await p.TotalAsync(http);
        foreach (var body in new[]
                 {
                     new JsonObject { ["code"] = "DF6", ["name"] = "No role", ["isCustomer"] = false, ["isSupplier"] = false },
                     new JsonObject { ["code"] = "DF6", ["name"] = "No role" },
                 })
        {
            if (named)
            {
                await RoleRequiredAsync(p.PostAsync(http, body));
                continue;
            }
            using var response = await p.PostAsync(http, body);
            Assert.Equal(new[] { "isCustomer", "isSupplier" }, McpAssert.ErrorKeys(await HttpAssert.ValidationAsync(response, "isCustomer", "isSupplier")));
        }
        Assert.Equal(before, await p.TotalAsync(http));
    }
}

/// <summary>
/// One of the four masters of spec 013 — <c>uom</c>, <c>article</c>, <c>partner</c>, <c>warehouse</c> — in one
/// tenant: its routes and tools, the "minimal" record of section 10 and the replace body that keeps a record as it
/// is. The rules of the four are the same test with different data (section 11).
/// </summary>
public sealed class RuledMaster
{
    private readonly Guid _baseUnit;

    private RuledMaster(string group, string path, Guid baseUnit)
    {
        Group = group;
        Path = path;
        _baseUnit = baseUnit;
    }

    /// <summary><c>uom</c>, <c>article</c>, <c>partner</c> or <c>warehouse</c>: the group of its rules and the prefix of its tools.</summary>
    public string Group { get; }

    public string Path { get; }

    public string CodeChangeable => MasterRules.CodeChangeable(Group);

    public string NameUnique => MasterRules.NameUnique(Group);

    /// <summary>Tool name for an operation: <c>create</c>, <c>update</c>.</summary>
    public string Tool(string operation) => $"{Group}_{operation}";

    /// <summary>The master in this tenant. An article needs a base unit: unit <c>base-u</c> is created once.</summary>
    public static async Task<RuledMaster> OpenAsync(string group, HttpClient http)
    {
        switch (group)
        {
            case "uom":
                return new RuledMaster(group, Uom.Path, Guid.Empty);
            case "partner":
                return new RuledMaster(group, MasterApi.Partners.Path, Guid.Empty);
            case "warehouse":
                return new RuledMaster(group, MasterApi.Warehouses.Path, Guid.Empty);
            case "article":
                using (var existing = await http.GetAsync($"{Uom.Path}/by-code/base-u"))
                {
                    var unit = existing.StatusCode == HttpStatusCode.OK
                        ? await HttpAssert.JsonAsync(existing, HttpStatusCode.OK)
                        : await Uom.CreateAsync(http, "base-u", "Base unit of the articles");
                    return new RuledMaster(group, Art.Path, unit.Id());
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(group), group, null);
        }
    }

    /// <summary>"Minimal" (section 10): the required properties of specs 001–004 only; a partner with <c>isSupplier: true</c>.</summary>
    public JsonObject Minimal(string code, string name)
    {
        var body = new JsonObject { ["code"] = code, ["name"] = name };
        if (Group == "article")
        {
            body["type"] = "stock";
            body["baseUnitId"] = _baseUnit.ToString();
        }
        if (Group == "partner")
            body["isSupplier"] = true;
        return body;
    }

    /// <summary>The replace body that keeps every value of the record ("its own body").</summary>
    public JsonObject BodyOf(JsonElement record) => Group switch
    {
        "uom" => new JsonObject { ["code"] = record.Str("code"), ["name"] = record.Str("name"), ["isActive"] = record.Bool("isActive") },
        "article" => Stock.ArticleBody(record),
        "partner" => Orders.PartnerBody(record),
        _ => Stock.WarehouseBody(record),
    };

    public Task<HttpResponseMessage> PostAsync(HttpClient client, JsonObject body) => client.PostAsJsonAsync(Path, body);

    public Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, JsonObject body) => client.PutAsJsonAsync($"{Path}/{id}", body);

    public Task<HttpResponseMessage> ByCodeAsync(HttpClient client, string code) =>
        client.GetAsync($"{Path}/by-code/{Uri.EscapeDataString(code)}");

    public async Task<JsonElement> CreateAsync(HttpClient client, JsonObject body)
    {
        using var response = await PostAsync(client, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.Created);
    }

    public async Task<JsonElement> ReplaceAsync(HttpClient client, Guid id, JsonObject body)
    {
        using var response = await PutAsync(client, id, body);
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    public async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"{Path}/{id}");
        return await HttpAssert.JsonAsync(response, HttpStatusCode.OK);
    }

    /// <summary><paramref name="query"/> starts with <c>?</c> or is empty.</summary>
    public Task<JsonElement> ListAsync(HttpClient client, string query = "?limit=500") => Stock.ListAsync(client, Path + query);

    public async Task<int> TotalAsync(HttpClient client) => (await ListAsync(client, "?limit=1")).Total();

    /// <summary>How many records of the resource have exactly this name.</summary>
    public async Task<int> CountNamedAsync(HttpClient client, string name) =>
        (await ListAsync(client)).Items().Count(i => i.Str("name") == name);

    /// <summary>Asserts that a refused write left the record exactly as it was (<c>updatedAt</c> included).</summary>
    public async Task AssertUnchangedAsync(HttpClient client, JsonElement before) =>
        McpAssert.JsonEqual(before, await GetAsync(client, before.Id()), $"The {Group} changed");

    public override string ToString() => Group;
}

using Xerp.Api.Http;
using Xerp.Application.Common;
using Xerp.Application.Users;

namespace Xerp.Api.Endpoints;

/// <summary>Spec U01: users, and signing in and out with username and password. HTTP only, like API keys: they carry secrets.</summary>
public static class UserEndpoints
{
    public const string Route = "/users";

    public static void MapUserEndpoints(this IEndpointRouteBuilder v1)
    {
        // The one route without a credential: ApiV1Middleware sets the tenant from {tenant} and no key.
        v1.MapPost("/login/{tenant}", async (string tenant, HttpContext http, LoginOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<LoginInput>(http.Request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.LoginAsync(body.Value, ct);
            if (!result.IsSuccess)
                return Problems.From(result.Error);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(result.Value);
        });

        v1.MapPost("/logout", async (LoginOperations operations, CancellationToken ct) =>
        {
            var result = await operations.LogoutAsync(ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        });

        var users = v1.MapGroup(Route);

        users.MapGet("", async (HttpRequest request, UserOperations operations, CancellationToken ct) =>
        {
            var binder = new QueryBinder(request.Query);
            var input = new ListUsersInput(binder.Text("search"), binder.Bool("isActive"), binder.Int("limit"), binder.Int("offset"));
            if (binder.Error is { } error)
                return Problems.From(error);
            var result = await operations.ListAsync(input, ct);
            return result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
        }).AcceptsQuery("search", "isActive", "limit", "offset");

        users.MapGet("/{id:guid}", async (Guid id, UserOperations operations, CancellationToken ct) =>
            Respond(await operations.GetAsync(id, ct)));

        users.MapPost("", async (HttpRequest request, UserOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<CreateUserInput>(request, ct);
            if (!body.IsSuccess)
                return Problems.From(body.Error);
            var result = await operations.CreateAsync(body.Value, ct);
            return result.IsSuccess ? Results.Created($"/api/v1{Route}/{result.Value.Id}", result.Value) : Problems.From(result.Error);
        });

        users.MapPut("/{id:guid}", async (Guid id, HttpRequest request, UserOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<UpdateUserInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.UpdateAsync(id, body.Value, ct)) : Problems.From(body.Error);
        });

        users.MapPost("/{id:guid}/password", async (Guid id, HttpRequest request, UserOperations operations, CancellationToken ct) =>
        {
            var body = await JsonBody.ReadAsync<SetPasswordInput>(request, ct);
            return body.IsSuccess ? Respond(await operations.SetPasswordAsync(id, body.Value, ct)) : Problems.From(body.Error);
        });

        users.MapPost("/{id:guid}/deactivate", async (Guid id, UserOperations operations, CancellationToken ct) =>
            Respond(await operations.DeactivateAsync(id, ct)));

        users.MapPost("/{id:guid}/activate", async (Guid id, UserOperations operations, CancellationToken ct) =>
            Respond(await operations.ActivateAsync(id, ct)));
    }

    private static IResult Respond<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problems.From(result.Error);
}

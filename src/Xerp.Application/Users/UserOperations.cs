using Microsoft.EntityFrameworkCore;
using Xerp.Application.Common;
using Xerp.Application.Ports;
using Xerp.Domain.Rules;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Users;

/// <summary>
/// User management (spec U01): list, get, create, rename, set a password, deactivate, activate. Users are never
/// deleted. Ending a user's sessions revokes their session keys. <see cref="IXerpDb"/> is filtered to the tenant.
/// </summary>
public sealed class UserOperations(IXerpDb db, ITenantContext context, IClock clock, IRules rules, IPasswordHasher hasher)
{
    private const string NotFoundDetail = "User not found.";

    public async Task<Result<PagedResult<UserDto>>> ListAsync(ListUsersInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        var query = UserValidation.List(errors, input);
        if (errors.Any)
            return errors.ToError();

        var users = db.Users.AsNoTracking();
        if (query.IsActive is { } isActive)
            users = users.Where(u => u.IsActive == isActive);
        if (query.Search is { } search)
        {
            var pattern = LikePattern.Contains(search);
            users = users.Where(u =>
                EF.Functions.Like(u.Username, DbText.Lower(pattern), LikePattern.EscapeCharacter)
                || EF.Functions.Like(DbText.Lower(u.Name), DbText.Lower(pattern), LikePattern.EscapeCharacter));
        }
        var total = await users.CountAsync(cancellationToken);
        var items = await users.OrderBy(u => u.Username).Skip(query.Offset).Take(query.Limit).ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        return new PagedResult<UserDto>(items.Select(u => ToDto(u, now)).ToList(), total, query.Limit, query.Offset);
    }

    public async Task<Result<UserDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? NotFound() : ToDto(user, clock.UtcNow);
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserInput input, CancellationToken cancellationToken = default)
    {
        var actor = ActorKeyId();
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var errors = new ValidationErrors();
        var username = UserValidation.Username(errors, input.Username);
        var name = errors.Name(input.Name);
        var password = UserValidation.Password(errors, input.Password, tenantRules[RuleRegistry.UsersShortPasswordsAllowed]);
        if (errors.Any)
            return errors.ToError();

        return await db.SerializedPerTenantAsync<Result<UserDto>>(async ct =>
        {
            if (await db.Users.AnyAsync(u => u.Username == username, ct))
                return new AppError(ErrorCodes.UsernameTaken, $"A user with username '{username}' already exists.",
                    new Dictionary<string, string[]> { ["username"] = ["This username is taken."] });
            var user = User.Create(username, name, hasher.Hash(password), clock.UtcNow, actor);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return ToDto(user, clock.UtcNow);
        }, cancellationToken);
    }

    public async Task<Result<UserDto>> UpdateAsync(Guid id, UpdateUserInput input, CancellationToken cancellationToken = default)
    {
        var actor = ActorKeyId();
        var errors = new ValidationErrors();
        var name = errors.Name(input.Name);
        if (errors.Any)
            return errors.ToError();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        user.Rename(name, clock.UtcNow, actor);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user, clock.UtcNow);
    }

    /// <summary>A new password unlocks the user and ends every session of theirs except the calling one.</summary>
    public async Task<Result<UserDto>> SetPasswordAsync(Guid id, SetPasswordInput input, CancellationToken cancellationToken = default)
    {
        var actor = ActorKeyId();
        var tenantRules = await rules.ReadAsync(cancellationToken);
        var errors = new ValidationErrors();
        var password = UserValidation.Password(errors, input.Password, tenantRules[RuleRegistry.UsersShortPasswordsAllowed]);
        if (errors.Any)
            return errors.ToError();

        return await db.SerializedPerTenantAsync<Result<UserDto>>(async ct =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
                return NotFound();
            var now = clock.UtcNow;
            user.SetPassword(hasher.Hash(password), now, actor);
            await db.SaveChangesAsync(ct);
            await EndSessionsAsync(user.Id, now, actor, keep: actor, ct);
            return ToDto(user, now);
        }, cancellationToken);
    }

    /// <summary>A deactivated user cannot sign in, and every session of theirs ends.</summary>
    public async Task<Result<UserDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var actor = ActorKeyId();
        return await db.SerializedPerTenantAsync<Result<UserDto>>(async ct =>
        {
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
                return NotFound();
            var now = clock.UtcNow;
            if (user.IsActive)
            {
                user.Deactivate(now, actor);
                await db.SaveChangesAsync(ct);
            }
            await EndSessionsAsync(user.Id, now, actor, keep: null, ct);
            return ToDto(user, now);
        }, cancellationToken);
    }

    public async Task<Result<UserDto>> ActivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var actor = ActorKeyId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (!user.IsActive)
        {
            user.Activate(clock.UtcNow, actor);
            await db.SaveChangesAsync(cancellationToken);
        }
        return ToDto(user, clock.UtcNow);
    }

    private Task<int> EndSessionsAsync(Guid userId, DateTime now, Guid actor, Guid? keep, CancellationToken cancellationToken) =>
        db.ApiKeys
            .Where(k => k.UserId == userId && k.IsActive && k.Id != keep)
            .ExecuteUpdateAsync(u => u
                .SetProperty(k => k.IsActive, false)
                .SetProperty(k => k.RevokedAt, (DateTime?)now)
                .SetProperty(k => k.RevokedBy, (Guid?)actor), cancellationToken);

    internal static UserDto ToDto(User u, DateTime now) =>
        new(u.Id, u.Username, u.Name, u.IsActive, u.IsLocked(now), u.IsLocked(now) ? u.LockedUntil : null, u.LastLoginAt,
            u.CreatedAt, u.CreatedBy, u.UpdatedAt, u.UpdatedBy);

    private Guid ActorKeyId() =>
        context.ApiKeyId ?? throw new InvalidOperationException("Users are managed with a tenant API key.");

    private static AppError NotFound() => AppError.NotFound(NotFoundDetail);
}

/// <summary>
/// Sign-in and sign-out (spec U01). The sign-in runs with the tenant named by the route and no key: the
/// middleware sets the tenant only. Every cause of failure answers LOGIN_FAILED alike, after the same work.
/// </summary>
public sealed class LoginOperations(
    IXerpDb db, ITenantContext context, ICurrentTenantReader currentTenant, IClock clock, IRules rules,
    IPasswordHasher hasher, IApiKeyGenerator keyGenerator, IApiKeyHasher keyHasher)
{
    private static string? _dummyHash;

    public async Task<Result<LoginDto>> LoginAsync(LoginInput input, CancellationToken cancellationToken = default)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(input.Username))
            errors.Add("username", "username is required.");
        if (string.IsNullOrEmpty(input.Password))
            errors.Add("password", "password is required.");
        if (errors.Any)
            return errors.ToError();
        var username = input.Username!.Trim().ToLowerInvariant();
        var password = input.Password!;

        if (context.TenantId is not { } tenantId || await currentTenant.ReadAsync(cancellationToken) is not { } tenant)
        {
            Dummy(password);
            return AppError.LoginFailed();
        }
        var tenantRules = await rules.ReadAsync(cancellationToken);

        var signedIn = await db.SerializedPerTenantAsync(async ct =>
        {
            var user = UserRules.IsValidUsername(username) ? await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct) : null;
            var now = clock.UtcNow;
            if (user is null || !user.IsActive || user.IsLocked(now))
            {
                Dummy(password);
                return null;
            }
            if (!hasher.Verify(user.PasswordHash, password))
            {
                user.RecordFailedLogin(now, tenantRules[RuleRegistry.UsersLockAfterFailedLogins]);
                await db.SaveChangesAsync(ct);
                return null;
            }
            user.RecordLogin(now);
            var secret = keyGenerator.Generate();
            var key = ApiKey.CreateSession(tenantId, user, keyHasher.Hash(secret), now);
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync(ct);
            return new LoginDto(secret, key.Id, key.ExpiresAt!.Value, new LoginUser(user.Id, user.Username, user.Name),
                new LoginTenant(tenant.Id, tenant.Code, tenant.Name));
        }, cancellationToken);

        return signedIn is null ? AppError.LoginFailed() : signedIn;
    }

    /// <summary>Ends the calling session. Only a key a sign-in issued can sign out; other keys are revoked through /api-keys.</summary>
    public async Task<Result<LogoutDto>> LogoutAsync(CancellationToken cancellationToken = default)
    {
        if (context.ApiKeyId is not { } keyId)
            return AppError.Unauthenticated();
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
            return AppError.Unauthenticated();
        if (!key.IsSession)
            return AppError.InvalidState("Only a key issued by a sign-in (POST /login/{tenant}) can sign out. Revoke other keys with POST /api-keys/{id}/revoke.");
        var now = clock.UtcNow;
        key.Revoke(now, key.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new LogoutDto(key.Id, key.RevokedAt ?? now);
    }

    private void Dummy(string password) => hasher.Verify(_dummyHash ??= hasher.Hash("not-a-password"), password);
}

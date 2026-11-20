using Xerp.Application.Common;
using Xerp.Domain.Rules;
using Xerp.Domain.Tenancy;

namespace Xerp.Application.Users;

/// <summary>A user as clients see it: never the password or its hash.</summary>
public sealed record UserDto(
    Guid Id,
    string Username,
    string Name,
    bool IsActive,
    bool IsLocked,
    DateTime? LockedUntil,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    Guid CreatedBy,
    DateTime UpdatedAt,
    Guid UpdatedBy);

public sealed record CreateUserInput(string? Username, string? Name, string? Password);

public sealed record UpdateUserInput(string? Name);

public sealed record SetPasswordInput(string? Password);

public sealed record ListUsersInput(string? Search = null, bool? IsActive = null, int? Limit = null, int? Offset = null);

public sealed record LoginInput(string? Username, string? Password);

public sealed record LoginUser(Guid Id, string Username, string Name);

public sealed record LoginTenant(Guid Id, string Code, string Name);

/// <summary>The answer to a sign-in: the session key (shown once) and when it expires.</summary>
public sealed record LoginDto(string Key, Guid ApiKeyId, DateTime ExpiresAt, LoginUser User, LoginTenant Tenant);

public sealed record LogoutDto(Guid ApiKeyId, DateTime RevokedAt);

internal sealed record UserListQuery(string? Search, bool? IsActive, int Limit, int Offset);

/// <summary>Input rules of spec U01. No I/O.</summary>
internal static class UserValidation
{
    public static string Username(ValidationErrors errors, string? input)
    {
        var username = input?.Trim().ToLowerInvariant();
        if (UserRules.IsValidUsername(username))
            return username!;
        errors.Add("username", string.IsNullOrWhiteSpace(input)
            ? "username is required."
            : $"username must be 1-{UserRules.UsernameMaxLength} characters: letters a-z, digits, '.', '_' or '-', starting with a letter or digit.");
        return "";
    }

    public static string Password(ValidationErrors errors, string? input, bool shortAllowed)
    {
        if (string.IsNullOrEmpty(input))
        {
            errors.Add("password", "password is required.");
            return "";
        }
        if (input.Length > UserRules.PasswordMaxLength)
            errors.Add("password", $"password must be at most {UserRules.PasswordMaxLength} characters.");
        else if (!shortAllowed && input.Length < UserRules.PasswordMinLength)
            errors.Add("password", $"password must be at least {UserRules.PasswordMinLength} characters.",
                RuleRegistry.UsersShortPasswordsAllowed, false);
        return input;
    }

    public static UserListQuery List(ValidationErrors errors, ListUsersInput input) =>
        new(ListRules.Search(errors, input.Search), input.IsActive, ListRules.Limit(errors, input.Limit), ListRules.Offset(errors, input.Offset));
}

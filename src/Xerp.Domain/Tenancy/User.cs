using System.Text.RegularExpressions;
using Xerp.Domain.Common;

namespace Xerp.Domain.Tenancy;

/// <summary>
/// A person who signs in to a tenant with username and password (spec U01). A sign-in issues a session: an
/// API key of actor type human that belongs to the user and expires (<see cref="ApiKey.UserId"/>,
/// <see cref="ApiKey.ExpiresAt"/>), so every write keeps pointing at a key as its actor, as before.
/// Users are never deleted, only deactivated: their sessions stay as the actors of what they did.
/// </summary>
public sealed partial class User : ITenantOwned
{
    private User() { }

    public Guid Id { get; private set; }

    /// <summary>Stamped by the DbContext from the current tenant when the row is inserted.</summary>
    public Guid TenantId { get; private set; }

    /// <summary>Lower case, unique within the tenant, never changed.</summary>
    public string Username { get; private set; } = "";

    public string Name { get; private set; } = "";

    /// <summary>PBKDF2 with a salt per password; the password itself is never stored.</summary>
    public string PasswordHash { get; private set; } = "";

    public bool IsActive { get; private set; } = true;

    /// <summary>Consecutive failed sign-ins since the last success, the last new password or the last lock.</summary>
    public int FailedLoginAttempts { get; private set; }

    public DateTime? LockedUntil { get; private set; }
    public DateTime? LastLoginAt { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    public static User Create(string username, string name, string passwordHash, DateTime now, Guid actorKeyId)
    {
        if (!UserRules.IsValidUsername(username))
            throw new ArgumentException("Invalid username.", nameof(username));
        return new User
        {
            Id = Guid.CreateVersion7(),
            Username = username,
            Name = NameRules.Normalize(name, nameof(name)),
            PasswordHash = passwordHash,
            IsActive = true,
            CreatedAt = now,
            CreatedBy = actorKeyId,
            UpdatedAt = now,
            UpdatedBy = actorKeyId,
        };
    }

    public void Rename(string name, DateTime now, Guid actorKeyId)
    {
        Name = NameRules.Normalize(name, nameof(name));
        Touch(now, actorKeyId);
    }

    /// <summary>A new password also unlocks the user and clears the failures; the caller ends its sessions.</summary>
    public void SetPassword(string passwordHash, DateTime now, Guid actorKeyId)
    {
        PasswordHash = passwordHash;
        FailedLoginAttempts = 0;
        LockedUntil = null;
        Touch(now, actorKeyId);
    }

    /// <summary>The caller ends its sessions.</summary>
    public void Deactivate(DateTime now, Guid actorKeyId)
    {
        IsActive = false;
        Touch(now, actorKeyId);
    }

    public void Activate(DateTime now, Guid actorKeyId)
    {
        IsActive = true;
        Touch(now, actorKeyId);
    }

    public bool IsLocked(DateTime now) => LockedUntil is { } until && until > now;

    /// <summary>A wrong password; with <paramref name="lockout"/>, the last allowed failure locks the user.</summary>
    public void RecordFailedLogin(DateTime now, bool lockout)
    {
        if (!lockout)
            return;
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= UserRules.MaxFailedLogins)
        {
            FailedLoginAttempts = 0;
            LockedUntil = now + UserRules.LockDuration;
        }
    }

    public void RecordLogin(DateTime now)
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    private void Touch(DateTime now, Guid actorKeyId)
    {
        UpdatedAt = now;
        UpdatedBy = actorKeyId;
    }
}

/// <summary>Invariants of users and sessions (spec U01). The configurable rules are in the rule registry.</summary>
public static partial class UserRules
{
    public const int UsernameMaxLength = 50;

    /// <summary>The shortest password unless the tenant allows short ones (users.shortPasswordsAllowed).</summary>
    public const int PasswordMinLength = 8;

    public const int PasswordMaxLength = 200;

    /// <summary>With users.lockAfterFailedLogins: this many wrong passwords in a row lock the user for <see cref="LockDuration"/>.</summary>
    public const int MaxFailedLogins = 5;

    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    /// <summary>How long a session key of a sign-in is valid.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>1 to 50 of a-z, 0-9, '.', '_', '-', starting with a letter or digit.</summary>
    public static bool IsValidUsername(string? username) =>
        username is { Length: >= 1 and <= UsernameMaxLength } && UsernamePattern().IsMatch(username);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}

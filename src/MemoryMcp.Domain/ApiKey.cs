namespace MemoryMcp.Domain;

/// <summary>
/// A credential belonging to exactly one <see cref="User"/>. One key per person (or per machine of
/// that person) rather than one key per team: a shared key would make the authorship recorded on
/// memories and documents meaningless, and would turn revocation into "rotate for everyone".
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; private set; }

    /// <summary>Owner of the credential. Required — there is no such thing as an unattributed key,
    /// because every write it performs is stamped with this user's id.</summary>
    public Guid UserId { get; private set; }

    public string KeyHash { get; private set; } = default!;
    public string KeyPrefix { get; private set; } = default!;

    /// <summary>What this credential is, not who owns it ("laptop", "ci", "claude-desktop") — the
    /// owner's identity lives on <see cref="User"/>.</summary>
    public string? Label { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    private ApiKey()
    {
    }

    public ApiKey(Guid userId, string keyHash, string keyPrefix, string? label = null)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        KeyHash = keyHash;
        KeyPrefix = keyPrefix;
        Label = label;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Revoke()
    {
        IsActive = false;
        RevokedAt = DateTimeOffset.UtcNow;
    }
}

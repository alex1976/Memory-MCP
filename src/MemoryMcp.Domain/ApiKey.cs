namespace MemoryMcp.Domain;

public sealed class ApiKey
{
    public Guid Id { get; private set; }
    public string KeyHash { get; private set; } = default!;
    public string KeyPrefix { get; private set; } = default!;
    public string? Label { get; private set; }
    public string? OwnerEmail { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    private ApiKey()
    {
    }

    public ApiKey(string keyHash, string keyPrefix, string? label = null, string? ownerEmail = null)
    {
        Id = Guid.NewGuid();
        KeyHash = keyHash;
        KeyPrefix = keyPrefix;
        Label = label;
        OwnerEmail = ownerEmail;
        IsActive = true;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Revoke()
    {
        IsActive = false;
        RevokedAt = DateTimeOffset.UtcNow;
    }
}

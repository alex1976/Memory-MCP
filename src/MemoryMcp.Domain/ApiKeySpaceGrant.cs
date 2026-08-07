namespace MemoryMcp.Domain;

public sealed class ApiKeySpaceGrant
{
    public Guid Id { get; private set; }
    public Guid ApiKeyId { get; private set; }
    public Guid SpaceId { get; private set; }
    public AccessLevel AccessLevel { get; private set; }
    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private ApiKeySpaceGrant()
    {
    }

    public ApiKeySpaceGrant(Guid apiKeyId, Guid spaceId, AccessLevel accessLevel, bool isDefault = false)
    {
        Id = Guid.NewGuid();
        ApiKeyId = apiKeyId;
        SpaceId = spaceId;
        AccessLevel = accessLevel;
        IsDefault = isDefault;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void SetAsDefault(bool isDefault) => IsDefault = isDefault;

    public bool Satisfies(AccessLevel required) => AccessLevel >= required;
}

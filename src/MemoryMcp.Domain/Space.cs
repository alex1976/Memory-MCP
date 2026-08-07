namespace MemoryMcp.Domain;

public sealed class Space
{
    public Guid Id { get; private set; }
    public string Key { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private Space()
    {
    }

    public Space(string key, string name, string? description = null)
    {
        Id = Guid.NewGuid();
        Key = key;
        Name = name;
        Description = description;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public void Update(string name, string? description)
    {
        Name = name;
        Description = description;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

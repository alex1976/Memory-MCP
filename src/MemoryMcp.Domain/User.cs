namespace MemoryMcp.Domain;

/// <summary>
/// A person. The authenticated principal is still an <see cref="ApiKey"/>, but a key is now a
/// *credential of* a user (laptop, CI, agent — one person may hold several), and it is the user that
/// carries the role and the authorship recorded on every write.
/// </summary>
public sealed class User
{
    public Guid Id { get; private set; }

    /// <summary>Normalized to lower case, and unique: it is the human-facing identity of the account and
    /// the key a provisioning path can look someone up by without knowing their id.</summary>
    public string Email { get; private set; } = default!;

    public string DisplayName { get; private set; } = default!;
    public UserRole Role { get; private set; }

    /// <summary>Deactivating a user rejects authentication for *all* their keys at once
    /// (see <c>ApiKeyRepository.FindActiveAccessByHashAsync</c>), which is the only offboarding step
    /// that doesn't require finding every credential they ever minted.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private User()
    {
    }

    public User(string email, string displayName, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("A user must have an email.", nameof(email));
        }

        Id = Guid.NewGuid();
        Email = NormalizeEmail(email);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Email : displayName.Trim();
        Role = role;
        IsActive = true;
        var now = DateTimeOffset.UtcNow;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public void ChangeRole(UserRole role)
    {
        Role = role;
        Touch();
    }

    public void Rename(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            DisplayName = displayName.Trim();
            Touch();
        }
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    private void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

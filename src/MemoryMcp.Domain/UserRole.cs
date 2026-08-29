namespace MemoryMcp.Domain;

/// <summary>
/// What a person is allowed to do anywhere they have access. Deliberately coarse — two types only —
/// and orthogonal to <see cref="ApiKeySpaceGrant"/>: the role is the ceiling the person can never
/// exceed, the grant is what they were given on one particular space. Effective access is the lower
/// of the two (see <see cref="UserRoleExtensions.MaxAccessLevel"/>), so demoting a Writer to Reader
/// removes write access everywhere at once without touching a single grant row.
/// </summary>
/// <remarks>
/// Persisted as a string, not an int (see <c>UserConfiguration</c>): the values are compared by
/// identity rather than by order, and a string column means a future role can be inserted without
/// renumbering rows already stored.
/// </remarks>
public enum UserRole
{
    /// <summary>May search and read in every space granted to them; may never write.</summary>
    Reader,

    /// <summary>May read and write in every space granted to them at <see cref="AccessLevel.ReadWrite"/>.</summary>
    Writer,
}

public static class UserRoleExtensions
{
    /// <summary>
    /// The highest <see cref="AccessLevel"/> this role can ever hold, regardless of what a space grant
    /// says. Applied where the access snapshot is built, so every downstream check — tools, services,
    /// <c>listSpaces</c>, <c>whoAmI</c> — sees one already-capped level and cannot disagree about it.
    /// </summary>
    public static AccessLevel MaxAccessLevel(this UserRole role) => role switch
    {
        UserRole.Writer => AccessLevel.ReadWrite,
        _ => AccessLevel.Read,
    };
}

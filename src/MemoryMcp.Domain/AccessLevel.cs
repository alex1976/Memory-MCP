namespace MemoryMcp.Domain;

/// <summary>
/// What a credential may do on one particular space. Compared with <c>&gt;=</c> (see
/// <see cref="ApiKeySpaceGrant.Satisfies"/>), so the numbering is meaningful and new values must be
/// appended rather than inserted.
/// </summary>
/// <remarks>
/// This is only half of the answer: the level a caller actually gets is the lower of the space grant
/// and the owning user's <see cref="UserRole"/> ceiling — a <see cref="UserRole.Reader"/> holding a
/// <see cref="ReadWrite"/> grant is still read-only.
/// </remarks>
public enum AccessLevel
{
    Read = 0,
    ReadWrite = 1,
}

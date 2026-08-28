using AwesomeAssertions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Tests.Domain;

public sealed class MemoryEdgeTests
{
    [Fact]
    public void Note_is_clamped_to_the_column_width_instead_of_overflowing_it()
    {
        // The note comes from an LLM, which can't be held to the length the prompt asks for. Persisting
        // it unclamped would throw on SaveChangesAsync — outside MemoryService's extraction fallback —
        // and take the whole add_memory save down with it.
        var tooLong = new string('x', MemoryEdge.NoteMaxLength + 50);

        var edge = new MemoryEdge(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RelationType.Updates, tooLong);

        edge.Note.Should().HaveLength(MemoryEdge.NoteMaxLength);
        edge.Note.Should().EndWith("…");
    }

    [Fact]
    public void Note_at_exactly_the_column_width_is_kept_verbatim()
    {
        var exact = new string('x', MemoryEdge.NoteMaxLength);

        var edge = new MemoryEdge(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RelationType.Extends, exact);

        edge.Note.Should().Be(exact);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_notes_are_normalized_to_null_so_callers_can_test_for_absence(string? note)
    {
        var edge = new MemoryEdge(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RelationType.Derives, note);

        edge.Note.Should().BeNull();
    }

    [Fact]
    public void Note_is_trimmed()
    {
        var edge = new MemoryEdge(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RelationType.Extends, "  adds the team size  ");

        edge.Note.Should().Be("adds the team size");
    }
}

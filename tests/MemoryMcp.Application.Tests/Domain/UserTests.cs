using AwesomeAssertions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Tests.Domain;

public sealed class UserTests
{
    [Theory]
    [InlineData(UserRole.Reader, AccessLevel.Read)]
    [InlineData(UserRole.Writer, AccessLevel.ReadWrite)]
    public void MaxAccessLevel_maps_each_role_to_its_ceiling(UserRole role, AccessLevel expected) =>
        role.MaxAccessLevel().Should().Be(expected);

    [Fact]
    public void Email_is_normalized_so_the_same_person_cannot_be_created_twice_by_casing()
    {
        var user = new User("  Ada@Example.COM ", "Ada", UserRole.Writer);

        user.Email.Should().Be("ada@example.com");
    }

    [Fact]
    public void DisplayName_falls_back_to_the_email_when_none_is_given()
    {
        var user = new User("ada@example.com", "   ", UserRole.Reader);

        user.DisplayName.Should().Be("ada@example.com");
    }

    [Fact]
    public void A_user_must_have_an_email()
    {
        var act = () => new User("  ", "Ada", UserRole.Writer);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ChangeRole_is_the_single_switch_that_makes_a_writer_read_only_everywhere()
    {
        var user = new User("ada@example.com", "Ada", UserRole.Writer);

        user.ChangeRole(UserRole.Reader);

        user.Role.MaxAccessLevel().Should().Be(AccessLevel.Read);
    }
}

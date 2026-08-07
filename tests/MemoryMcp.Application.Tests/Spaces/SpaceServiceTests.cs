using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Spaces;
using MemoryMcp.Application.Tests.TestSupport;
using MemoryMcp.Domain;
using NSubstitute;

namespace MemoryMcp.Application.Tests.Spaces;

public sealed class SpaceServiceTests
{
    private readonly ISpaceRepository _spaceRepository = Substitute.For<ISpaceRepository>();

    [Fact]
    public async Task ListSpacesAsync_merges_grants_with_counts()
    {
        var spaceId = Guid.NewGuid();
        var grant = new SpaceGrant(spaceId, "default", "Default", AccessLevel.ReadWrite, IsDefault: true);
        var accessContext = new FakeAccessContext { Grants = [grant] };

        _spaceRepository.GetCountsAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids != null && ids.Contains(spaceId)), Arg.Any<CancellationToken>())
            .Returns(new[] { new SpaceCounts(spaceId, DocumentCount: 3, MemoryCount: 7) });

        var service = new SpaceService(_spaceRepository, accessContext);

        var result = await service.ListSpacesAsync();

        result.Should().ContainSingle();
        result[0].Key.Should().Be("default");
        result[0].DocumentCount.Should().Be(3);
        result[0].MemoryCount.Should().Be(7);
        result[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task WhoAmIAsync_reports_active_space_and_full_space_list()
    {
        var spaceId = Guid.NewGuid();
        var grant = new SpaceGrant(spaceId, "default", "Default", AccessLevel.Read, IsDefault: true);
        var accessContext = new FakeAccessContext { Grants = [grant], OwnerLabel = "dev-key" };

        _spaceRepository.GetCountsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SpaceCounts>());

        var service = new SpaceService(_spaceRepository, accessContext);

        var result = await service.WhoAmIAsync();

        result.ApiKeyId.Should().Be(accessContext.ApiKeyId);
        result.Label.Should().Be("dev-key");
        result.ActiveSpaceKey.Should().Be("default");
        result.Spaces.Should().ContainSingle(s => s.Key == "default");
    }
}

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
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    [Fact]
    public async Task ListSpacesAsync_merges_grants_with_counts()
    {
        var spaceId = Guid.NewGuid();
        var grant = new SpaceGrant(spaceId, "default", "Default", AccessLevel.ReadWrite, IsDefault: true);
        var accessContext = new FakeAccessContext { Grants = [grant] };

        _spaceRepository.GetCountsAsync(Arg.Is<IReadOnlyList<Guid>>(ids => ids != null && ids.Contains(spaceId)), Arg.Any<CancellationToken>())
            .Returns(new[] { new SpaceCounts(spaceId, DocumentCount: 3, MemoryCount: 7) });

        var service = new SpaceService(_spaceRepository, accessContext, _unitOfWork);

        var result = await service.ListSpacesAsync();

        result.Should().ContainSingle();
        result[0].Key.Should().Be("default");
        result[0].DocumentCount.Should().Be(3);
        result[0].MemoryCount.Should().Be(7);
        result[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task WhoAmIAsync_reports_the_user_behind_the_key_alongside_the_space_list()
    {
        var spaceId = Guid.NewGuid();
        var grant = new SpaceGrant(spaceId, "default", "Default", AccessLevel.Read, IsDefault: true);
        var reader = new CurrentUser(Guid.NewGuid(), "reader@team.test", "Rita Reader", UserRole.Reader);
        var accessContext = new FakeAccessContext { Grants = [grant], OwnerLabel = "laptop", User = reader };

        _spaceRepository.GetCountsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SpaceCounts>());

        var service = new SpaceService(_spaceRepository, accessContext, _unitOfWork);

        var result = await service.WhoAmIAsync();

        result.ApiKeyId.Should().Be(accessContext.ApiKeyId);
        // The label describes the credential; the identity is the user.
        result.Label.Should().Be("laptop");
        result.UserId.Should().Be(reader.Id);
        result.UserEmail.Should().Be("reader@team.test");
        result.UserDisplayName.Should().Be("Rita Reader");
        result.UserRole.Should().Be("Reader");
        result.ActiveSpaceKey.Should().Be("default");
        result.Spaces.Should().ContainSingle(s => s.Key == "default");
    }

    [Fact]
    public async Task ListSpacesAsync_lists_every_space_the_key_is_granted()
    {
        var personal = new SpaceGrant(Guid.NewGuid(), "personal", "Personal", AccessLevel.ReadWrite, IsDefault: true);
        var team = new SpaceGrant(Guid.NewGuid(), "team", "Team", AccessLevel.ReadWrite, IsDefault: false);
        var readOnly = new SpaceGrant(Guid.NewGuid(), "archive", "Archive", AccessLevel.Read, IsDefault: false);
        var accessContext = new FakeAccessContext { Grants = [personal, team, readOnly] };

        _spaceRepository.GetCountsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SpaceCounts>());

        var service = new SpaceService(_spaceRepository, accessContext, _unitOfWork);

        var result = await service.ListSpacesAsync();

        // One key, N spaces, each with its own level — and the level reported is the effective one.
        result.Should().HaveCount(3);
        result.Should().ContainSingle(s => s.Key == "archive" && s.AccessLevel == "Read");
        result.Should().ContainSingle(s => s.Key == "team" && s.AccessLevel == "ReadWrite" && !s.IsDefault);
    }

    [Fact]
    public async Task SetActiveSpaceAsync_flips_default_onto_the_target_grant()
    {
        var currentSpaceId = Guid.NewGuid();
        var targetSpaceId = Guid.NewGuid();
        var currentGrant = new SpaceGrant(currentSpaceId, "current", "Current", AccessLevel.ReadWrite, IsDefault: true);
        var targetGrant = new SpaceGrant(targetSpaceId, "target", "Target", AccessLevel.Read, IsDefault: false);
        var accessContext = new FakeAccessContext { Grants = [currentGrant, targetGrant] };

        var currentEntity = new ApiKeySpaceGrant(accessContext.ApiKeyId, currentSpaceId, AccessLevel.ReadWrite, isDefault: true);
        var targetEntity = new ApiKeySpaceGrant(accessContext.ApiKeyId, targetSpaceId, AccessLevel.Read, isDefault: false);
        _spaceRepository.GetGrantsForApiKeyAsync(accessContext.ApiKeyId, Arg.Any<CancellationToken>())
            .Returns(new List<ApiKeySpaceGrant> { currentEntity, targetEntity });
        _spaceRepository.GetCountsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SpaceCounts>());

        var service = new SpaceService(_spaceRepository, accessContext, _unitOfWork);

        var result = await service.SetActiveSpaceAsync("target");

        currentEntity.IsDefault.Should().BeFalse();
        targetEntity.IsDefault.Should().BeTrue();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        result.Should().ContainSingle(s => s.Key == "target" && s.IsDefault);
        result.Should().ContainSingle(s => s.Key == "current" && !s.IsDefault);
    }

    [Fact]
    public async Task SetActiveSpaceAsync_throws_when_key_has_no_grant_for_the_space()
    {
        var accessContext = new FakeAccessContext { Grants = [] };
        var service = new SpaceService(_spaceRepository, accessContext, _unitOfWork);

        var act = async () => await service.SetActiveSpaceAsync("unknown");

        await act.Should().ThrowAsync<SpaceNotFoundException>();
    }
}

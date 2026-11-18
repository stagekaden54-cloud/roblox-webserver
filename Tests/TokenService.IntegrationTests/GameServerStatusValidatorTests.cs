using Common;
using Xunit;

namespace TokenService.IntegrationTests;

public sealed class GameServerStatusValidatorTests
{
    [Fact]
    public void RunningServerWithValidPlaceAndPort_IsJoinable()
    {
        Assert.True(GameServerStatusValidator.IsJoinable(123, 53640, "running"));
    }

    [Fact]
    public void RunningStatus_IsCaseInsensitive()
    {
        Assert.True(GameServerStatusValidator.IsJoinable(123, 53640, "RUNNING"));
    }

    [Theory]
    [InlineData(true, 123, 123, true)]
    [InlineData(false, 123, 123, false)]
    [InlineData(true, 456, 123, false)]
    [InlineData(true, 0, 123, false)]
    [InlineData(true, 123, 0, false)]
    public void ServerMustBeAliveAndBoundToRequestedPlace(
        bool serverAlive, long serverPlaceId, long requestedPlaceId, bool expected)
    {
        Assert.Equal(expected, GameServerStatusValidator.IsBoundToPlace(
            serverAlive, serverPlaceId, requestedPlaceId));
    }

    [Theory]
    [InlineData(0, 53640, "running")]
    [InlineData(-1, 53640, "running")]
    [InlineData(123, 0, "running")]
    [InlineData(123, -1, "running")]
    [InlineData(123, 65536, "running")]
    [InlineData(123, 53640, "starting")]
    [InlineData(123, 53640, "shutting_down")]
    [InlineData(123, 53640, null)]
    public void InvalidServerMetadata_IsNotJoinable(long placeId, int port, string? status)
    {
        Assert.False(GameServerStatusValidator.IsJoinable(placeId, port, status));
    }
}

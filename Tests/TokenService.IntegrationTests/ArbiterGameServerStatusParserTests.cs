using Common;
using Xunit;

namespace TokenService.IntegrationTests;

public sealed class ArbiterGameServerStatusParserTests
{
    [Fact]
    public void ParsesValidRunningServerStatus()
    {
        const string json = """{"gameId":"job-123","placeId":123,"port":53640,"status":"running"}""";

        Assert.True(ArbiterGameServerStatusParser.TryParse(json, out var placeId, out var port));
        Assert.Equal(123, placeId);
        Assert.Equal(53640, port);
    }

    [Theory]
    [InlineData("""{"placeId":0,"port":53640,"status":"running"}""")]
    [InlineData("""{"placeId":123,"port":0,"status":"running"}""")]
    [InlineData("""{"placeId":123,"port":65536,"status":"running"}""")]
    [InlineData("""{"placeId":123,"port":53640,"status":"starting"}""")]
    [InlineData("""{"placeId":123,"port":53640,"status":"stopped"}""")]
    [InlineData("""{"placeId":"123","port":53640,"status":"running"}""")]
    [InlineData("""{"placeId":123,"port":"53640","status":"running"}""")]
    [InlineData("""{"placeId":123,"port":53640}""")]
    [InlineData("""{"placeId":123,"port":53640,"status":true}""")]
    [InlineData("""[]""")]
    [InlineData("""not-json""")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsInvalidOrIncompleteStatus(string? json)
    {
        Assert.False(ArbiterGameServerStatusParser.TryParse(json, out var placeId, out var port));
        Assert.Equal(0, placeId);
        Assert.Equal(0, port);
    }

    [Fact]
    public void AcceptsRunningStatusWithoutCaseSensitivity()
    {
        const string json = """{"placeId":123,"port":53640,"status":"RUNNING"}""";

        Assert.True(ArbiterGameServerStatusParser.TryParse(json, out _, out _));
    }
}

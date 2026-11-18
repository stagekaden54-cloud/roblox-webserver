using System;
using System.Text.Json;

namespace Common
{
    /// <summary>
    /// Parses the read-only game-server status response returned by the Arbiter.
    /// </summary>
    public static class ArbiterGameServerStatusParser
    {
        public static bool TryParse(string? json, out long placeId, out int port)
        {
            placeId = 0;
            port = 0;

            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("placeId", out var placeElement) ||
                    placeElement.ValueKind != JsonValueKind.Number ||
                    !placeElement.TryGetInt64(out var parsedPlaceId) ||
                    !root.TryGetProperty("port", out var portElement) ||
                    portElement.ValueKind != JsonValueKind.Number ||
                    !portElement.TryGetInt32(out var parsedPort) ||
                    !root.TryGetProperty("status", out var statusElement) ||
                    statusElement.ValueKind != JsonValueKind.String ||
                    !GameServerStatusValidator.IsJoinable(
                        parsedPlaceId, parsedPort, statusElement.GetString()))
                {
                    return false;
                }

                placeId = parsedPlaceId;
                port = parsedPort;
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}

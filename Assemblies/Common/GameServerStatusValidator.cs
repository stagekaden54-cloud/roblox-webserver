namespace Common
{
    /// <summary>
    /// Validates the Arbiter fields required before a game server can be joined.
    /// </summary>
    public static class GameServerStatusValidator
    {
        public static bool IsJoinable(long placeId, int port, string? status)
        {
            return placeId > 0
                && port > 0
                && port <= 65535
                && string.Equals(status, "running", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}

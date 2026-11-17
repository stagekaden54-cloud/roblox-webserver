using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Games
{
    /// <summary>
    /// Unified token service handling persistent session tokens and short-lived game tickets.
    /// Registered-user game tickets are persisted in the database; guest tickets are process-local.
    /// </summary>
    public class TokenService
    {
        private readonly string _connectionString;
        private readonly string _publicBaseUrl;
        private readonly ConcurrentDictionary<string, GameTicket> _guestTickets = new();

        public TokenService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("Default") 
                ?? throw new ArgumentNullException("Database connection string not configured");
            _publicBaseUrl = configuration["PublicBaseUrl"] ?? string.Empty;
            _ = EnsureSessionsTable();
        }

        private async Task EnsureSessionsTable()
        {
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                using var cmd = new NpgsqlCommand(@"
                    CREATE TABLE IF NOT EXISTS sessions (
                        token       text            primary key,
                        user_id     bigint          not null,
                        created_at  timestamptz     not null default now(),
                        expires_at  timestamptz     not null,
                        last_ip     inet            null
                    )", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex) { Console.WriteLine($"[ERROR] EnsureSessionsTable: {ex}"); }
        }

        /// <summary>
        /// Creates a persistent session token for web authentication.
        /// These tokens are stored in the database and last for 1 year.
        /// </summary>
        public async Task<string> CreateSessionAsync(long userId, string? ipAddress = null)
        {
            var token = GenerateToken("sess_");
            var expiresAt = DateTimeOffset.UtcNow.AddYears(1);

            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
                INSERT INTO sessions (token, user_id, created_at, expires_at, last_ip)
                VALUES (@token, @user_id, NOW(), @expires_at, @ip)
                ON CONFLICT (token) DO UPDATE SET
                    user_id = EXCLUDED.user_id,
                    expires_at = EXCLUDED.expires_at,
                    last_ip = EXCLUDED.last_ip", conn);

            cmd.Parameters.AddWithValue("token", token);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
            cmd.Parameters.AddWithValue("ip", ipAddress != null ? (object)IPAddress.Parse(ipAddress) : DBNull.Value);

            await cmd.ExecuteNonQueryAsync();
            return token;
        }

        /// <summary>
        /// Validates a session token and returns the user ID if valid.
        /// Also extends the session expiration on successful validation (rate-limited to once per hour).
        /// </summary>
        public async Task<long?> ValidateSessionAsync(string token)
        {
            if (string.IsNullOrEmpty(token))
                return null;

            return await ValidateDbSessionAsync(token);
        }

        private async Task<long?> ValidateDbSessionAsync(string token)
        {
            var userId = await LookupSessionByTokenAsync(token);
            if (userId.HasValue)
                await TryExtendSessionAsync(token);
            return userId;
        }

        private async Task<long?> LookupSessionByTokenAsync(string token)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
                SELECT user_id FROM sessions 
                WHERE token = @token 
                AND (expires_at IS NULL OR expires_at > NOW() AT TIME ZONE 'utc')
                LIMIT 1", conn);

            cmd.Parameters.AddWithValue("token", token);
            var result = await cmd.ExecuteScalarAsync();

            if (result is long userId)
                return userId;

            if (result is int intId)
                return (long)intId;

            return null;
        }


        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastExtended = new();

        private async Task TryExtendSessionAsync(string token)
        {
            if (_lastExtended.TryGetValue(token, out var lastExtend) && lastExtend > DateTime.UtcNow.AddHours(-1))
                return;

            _lastExtended[token] = DateTime.UtcNow;
            await ExtendSessionAsync(token);
        }

        /// <summary>
        /// Extends session expiration by updating last activity.
        /// </summary>
        public async Task ExtendSessionAsync(string token)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
                UPDATE sessions 
                SET expires_at = NOW() AT TIME ZONE 'utc' + INTERVAL '1 year'
                WHERE token = @token", conn);
            
            cmd.Parameters.AddWithValue("token", token);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Returns the number of consecutive UTC calendar days (ending today or
        /// yesterday) on which the user had at least one session row.
        /// </summary>
        public async Task<int> GetConsecutiveLoginDaysAsync(long userId)
        {
            if (userId <= 0)
                return 0;

            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
WITH days AS (
    SELECT DISTINCT (created_at AT TIME ZONE 'utc')::date AS d
    FROM sessions
    WHERE user_id = @uid
),
streak AS (
    SELECT d, (d - (ROW_NUMBER() OVER (ORDER BY d DESC))::int) AS grp
    FROM days
)
SELECT count(*) FROM streak
WHERE grp = (SELECT grp FROM streak ORDER BY d DESC LIMIT 1)", conn);

            cmd.Parameters.AddWithValue("uid", userId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result is DBNull ? 0 : Convert.ToInt32(result);
        }

        /// <summary>
        /// Revokes a session token immediately.
        /// </summary>
        public async Task RevokeSessionAsync(string token)
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
                DELETE FROM sessions WHERE token = @token", conn);
            
            cmd.Parameters.AddWithValue("token", token);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Creates a short-lived game ticket. Registered-user tickets are persisted
        /// before the token is returned so an immediate authentication request cannot
        /// race a background database insert. Guest tickets remain process-local until
        /// the separate guest-identity work is completed.
        /// </summary>
        public async Task<string> CreateGameTicketAsync(long userId, long placeId, string? jobId = null)
        {
            var token = GenerateToken("game_");
            var now = DateTimeOffset.UtcNow;

            // Guest tickets are intentionally process-local for now. Opportunistically
            // prune expired entries so abandoned guest joins cannot grow this dictionary forever.
            foreach (var entry in _guestTickets)
            {
                if (entry.Value.ExpiresAt <= now)
                    _guestTickets.TryRemove(entry.Key, out _);
            }

            var ticket = new GameTicket
            {
                Token = token,
                UserId = userId,
                PlaceId = placeId,
                JobId = jobId,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(15),
                IsUsed = false
            };

            if (userId <= 0)
            {
                _guestTickets[token] = ticket;
                return token;
            }

            var authenticationUrl = $"{_publicBaseUrl}/Game/PlaceLauncher.ashx?request=AuthenticateTicket";
            var joinScriptUrl = $"{_publicBaseUrl}/Game/PlaceLauncher.ashx?request=RequestGame&placeId={placeId}&isPartyLeader=false&gender=&isTeleport=true";

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO authentication_tickets
                    (ticket_token, user_id, place_id, authentication_url, join_script_url,
                     created_at, expires_at, is_active, memory_cached, cache_expires_at, ticket_type)
                VALUES
                    (@token, @user_id, @place_id, @auth_url, @join_url,
                     @created_at, @expires_at, true, false, NULL, 'game_session')", conn);

            cmd.Parameters.AddWithValue("token", token);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("place_id", placeId > 0 ? (object)placeId : DBNull.Value);
            cmd.Parameters.AddWithValue("auth_url", authenticationUrl);
            cmd.Parameters.AddWithValue("join_url", joinScriptUrl);
            cmd.Parameters.AddWithValue("created_at", now.UtcDateTime);
            cmd.Parameters.AddWithValue("expires_at", ticket.ExpiresAt.UtcDateTime);
            await cmd.ExecuteNonQueryAsync();

            return token;
        }

        /// <summary>
        /// Reads a game ticket without consuming it. Use ConsumeGameTicketAsync
        /// for authentication so only one concurrent request can redeem a ticket.
        /// </summary>
        public async Task<GameTicket?> ValidateGameTicketAsync(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("game_", StringComparison.Ordinal))
                return null;

            if (_guestTickets.TryGetValue(token, out var guestTicket))
                return guestTicket.ExpiresAt > DateTimeOffset.UtcNow ? guestTicket : null;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"
                SELECT user_id, COALESCE(place_id, 0), created_at, expires_at, used_at
                FROM authentication_tickets
                WHERE ticket_token = @token
                  AND ticket_type = 'game_session'
                  AND is_active = true
                  AND used_at IS NULL
                  AND expires_at > NOW()
                LIMIT 1", conn);
            cmd.Parameters.AddWithValue("token", token);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new GameTicket
            {
                Token = token,
                UserId = reader.GetInt64(0),
                PlaceId = reader.GetInt64(1),
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                IsUsed = !reader.IsDBNull(4)
            };
        }

        /// <summary>
        /// Atomically consumes a game ticket. For persisted tickets, the conditional
        /// UPDATE ... RETURNING is the single-use gate across concurrent requests and
        /// multiple application instances. Guest tickets are process-local for now.
        /// </summary>
        public async Task<GameTicket?> ConsumeGameTicketAsync(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("game_", StringComparison.Ordinal))
                return null;

            if (_guestTickets.TryRemove(token, out var guestTicket))
            {
                return guestTicket.ExpiresAt > DateTimeOffset.UtcNow ? guestTicket : null;
            }

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"
                UPDATE authentication_tickets
                SET used_at = NOW(),
                    is_active = false
                WHERE ticket_token = @token
                  AND ticket_type = 'game_session'
                  AND is_active = true
                  AND used_at IS NULL
                  AND expires_at > NOW()
                RETURNING user_id, COALESCE(place_id, 0), created_at, expires_at", conn);
            cmd.Parameters.AddWithValue("token", token);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new GameTicket
            {
                Token = token,
                UserId = reader.GetInt64(0),
                PlaceId = reader.GetInt64(1),
                CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                IsUsed = true
            };
        }

        /// <summary>
        /// Cleans up expired game tickets from the database shadow table.
        /// This should be called periodically by a background service.
        /// </summary>
        public async Task CleanupExpiredTicketsAsync()
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(@"
                DELETE FROM authentication_tickets 
                WHERE memory_cached = true 
                AND cache_expires_at < NOW() - INTERVAL '1 hour'", conn);

            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Cleans up inactive sessions that haven't been used in over 1 year.
        /// </summary>
        public async Task CleanupInactiveSessionsAsync()
        {
            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = new NpgsqlCommand(@"
                DELETE FROM sessions 
                WHERE expires_at < NOW() - INTERVAL '30 days'", conn);

            await cmd.ExecuteNonQueryAsync();
        }





        private static string GenerateToken(string prefix)
        {
            using var rng = RandomNumberGenerator.Create();
            var tokenBytes = new byte[32];
            rng.GetBytes(tokenBytes);
            var base64 = Convert.ToBase64String(tokenBytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
            return prefix + base64;
        }
    }

    public class GameTicket
    {
        public string Token { get; set; } = string.Empty;
        public long UserId { get; set; }
        public long PlaceId { get; set; }
        public string? JobId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool IsUsed { get; set; }
    }
}


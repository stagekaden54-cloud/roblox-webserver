using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameTokenService = Games.TokenService;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace TokenService.IntegrationTests;

public sealed class GameTicketConsumptionTests : IAsyncLifetime
{
    private readonly string _adminConnectionString =
        Environment.GetEnvironmentVariable("TEST_DATABASE")
        ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    private readonly string _schema = "ticket_tests_" + Guid.NewGuid().ToString("N");
    private string _testConnectionString = string.Empty;
    private GameTokenService _service = null!;

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", admin);
            await createSchema.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            SearchPath = _schema
        };
        _testConnectionString = builder.ConnectionString;

        await using (var conn = new NpgsqlConnection(_testConnectionString))
        {
            await conn.OpenAsync();
            const string schemaSql = @"
                CREATE TABLE users (user_id BIGINT PRIMARY KEY);
                CREATE TABLE assets (asset_id BIGINT PRIMARY KEY);
                CREATE TABLE authentication_tickets (
                    ticket_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    ticket_token TEXT NOT NULL UNIQUE,
                    user_id BIGINT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
                    place_id BIGINT NULL REFERENCES assets(asset_id) ON DELETE CASCADE,
                    authentication_url TEXT NOT NULL,
                    join_script_url TEXT NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    expires_at TIMESTAMPTZ NOT NULL,
                    used_at TIMESTAMPTZ NULL,
                    is_active BOOLEAN NOT NULL,
                    memory_cached BOOLEAN NOT NULL DEFAULT false,
                    cache_expires_at TIMESTAMPTZ NULL,
                    ticket_type TEXT NOT NULL
                );
                INSERT INTO users(user_id) VALUES (42);
                INSERT INTO assets(asset_id) VALUES (100);";
            await using var setup = new NpgsqlCommand(schemaSql, conn);
            await setup.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _testConnectionString,
                ["PublicBaseUrl"] = "https://block16.example"
            })
            .Build();
        _service = new GameTokenService(configuration);
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", admin);
        await dropSchema.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RegisteredTicket_IsConsumedExactlyOnceUnderConcurrency()
    {
        var token = await _service.CreateGameTicketAsync(42, 100);
        var attempts = Enumerable.Range(0, 16)
            .Select(_ => _service.ConsumeGameTicketAsync(token));

        var results = await Task.WhenAll(attempts);
        var successful = results.Where(ticket => ticket is not null).ToArray();

        Assert.Single(successful);
        Assert.Equal(42, successful[0]!.UserId);
        Assert.Equal(100, successful[0]!.PlaceId);
        Assert.True(successful[0]!.IsUsed);
    }

    [Fact]
    public async Task ExpiredRegisteredTicket_CannotBeConsumed()
    {
        var token = await _service.CreateGameTicketAsync(42, 100);
        await using var conn = new NpgsqlConnection(_testConnectionString);
        await conn.OpenAsync();
        await using var expire = new NpgsqlCommand(
            "UPDATE authentication_tickets SET expires_at = NOW() - INTERVAL '1 minute' WHERE ticket_token = @token", conn);
        expire.Parameters.AddWithValue("token", token);
        await expire.ExecuteNonQueryAsync();

        Assert.Null(await _service.ConsumeGameTicketAsync(token));
    }

    [Fact]
    public async Task GuestTicket_CanOnlyBeConsumedOnce()
    {
        var token = await _service.CreateGameTicketAsync(0, 100);

        var first = await _service.ConsumeGameTicketAsync(token);
        var second = await _service.ConsumeGameTicketAsync(token);

        Assert.NotNull(first);
        Assert.Equal(0, first!.UserId);
        Assert.Null(second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-game-ticket")]
    public async Task InvalidToken_IsRejected(string token)
    {
        Assert.Null(await _service.ConsumeGameTicketAsync(token));
    }
}

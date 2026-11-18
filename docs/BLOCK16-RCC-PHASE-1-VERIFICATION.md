# BLOCK16 RCC Phase 1 — Offline Verification Plan

## Goal

Verify the BLOCK16-to-Arbiter contract and the backend's launch/authentication safeguards without requiring a Windows RCC host or a live game client.

This phase is not proof that RCC can start a real game or that a client can join. Those checks remain Phase 2.

## Source-verified Arbiter contract

The current Arbiter implementation in `Arbiter/Program.cs` exposes:

- `GET /api/gameservers/{gameId}/status`
- A successful JSON response containing `gameId`, `placeId`, `port`, and `status` (plus player-count and lifecycle metadata).
- `GET /api/gameservers/by-place/{placeId}` for listing servers associated with a place.

The website launcher reads the status endpoint and must treat Arbiter's place ID and port as authoritative. A caller-supplied port must not override the Arbiter response.

## Automated checks

The existing `.github/workflows/game-ticket-verification.yml` workflow builds the website and runs the PostgreSQL-backed ticket tests. The repository's .NET workflow also builds and tests the solution on Windows.

The ticket integration tests cover:

- Registered ticket consumption exactly once under concurrent requests.
- Expired registered tickets being rejected.
- Guest tickets using the `game_` format and being single-use within the running process.
- Invalid ticket strings being rejected.

The game-server status tests cover:

- Positive place IDs and valid TCP port range (1–65535).
- Only `running` status being joinable, case-insensitively.
- The server being alive and bound to the requested place.
- Parsing the Arbiter JSON response and rejecting missing, malformed, wrongly typed, stopped, or invalid metadata.

## Manual checks possible without RCC

1. Confirm the website's configured Arbiter host and port point to the intended private Arbiter service in the test environment.
2. Confirm the Arbiter health endpoint responds before testing the website-to-Arbiter connection.
3. Use a non-production test environment and a controlled fake/stub status response to verify the website accepts a valid running response.
4. Verify the website rejects a missing job, stopped server, invalid port, malformed JSON, and mismatched place ID.
5. Verify the launcher uses the port returned by Arbiter rather than any port supplied by the caller.
6. Run the registered-user and guest ticket flows against the test database; do not paste credentials, cookies, tickets, or tokens into issue comments or logs.

Do not call mutating Arbiter management endpoints merely to check connectivity. The status and health endpoints are the appropriate read-only checks.

## Known limitations and safety boundaries

- There is no Windows RCC host available for this phase.
- A successful Arbiter health/status response does not prove that an RCC process is healthy or that a client can join.
- Guest tickets are currently process-local. They are not yet suitable for reliable multi-instance deployments.
- Game presence must not be treated as authoritative solely because a client submits a user ID or place ID. Phase 2 must validate presence using server-side evidence.
- Keep Arbiter management endpoints private and authenticated. Do not expose the Arbiter management API directly to the public internet.
- Do not put database connection strings, private keys, session cookies, or authentication tickets into source control or CI output.

## Phase 2 exit criteria

Only mark real RCC testing complete after a compatible Windows RCC build has been started on a reachable Windows host, a test job is reported as running with the expected place ID and port, a compatible client successfully joins, and server-verified presence is observed.

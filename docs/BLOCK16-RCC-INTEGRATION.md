# BLOCK16 RCC integration plan

## Status

Planning and safety gate only. This document does not enable RCC, expose an Arbiter endpoint, change the live BLOCK16 website, or claim that any RCC build is compatible.

## Target architecture

- Keep the BLOCK16 website and its existing Cloudflare Worker deployment separate from RCC process hosting.
- Run the Arbiter and RCC processes on a dedicated, supported Windows host.
- Keep RCC's control interface private to the host or a tightly restricted private network. Do not publish the RCC SOAP/HTTP listener directly to the internet.
- Allow the public website/API to request game allocation only through a separately authenticated and narrowly scoped game-server API.
- Advertise a client-reachable game-server address; loopback addresses such as 127.0.0.1 are valid only for same-host testing.

## Confirmed source-level findings

These findings were confirmed by reviewing the current `main` branch. They are code-review findings, not live RCC test results.

1. **Protocol compatibility is unverified.** The current RCC client code uses SOAP methods such as `GetVersion` and `OpenJobEx`. The repository's JSON mode changes the job payload, but that does not by itself prove that the transport is compatible with a JSON-only RCC service. Test against the exact RCC build and matching client build before enabling allocation.
2. **Guest authentication response bug.** `PlaceLauncherController.HandleRequestGame` initializes `ticketToken` to an empty string and later uses `ticketToken ?? guestToken`. An empty string is not null, so the guest token fallback is never selected. The generated guest string is also not a secure credential.
3. **Job/place binding.** `PlaceLauncherController` accepts a caller-supplied job ID after checking its status/port, but the inspected path does not verify that the job belongs to the requested place. The allocation/join path must verify job ID, place ID, and server state together before issuing join data.
4. **Ticket replay and atomic consumption.** `AuthenticationTicketService.ValidateTicketAsync` filters on `is_active = true` and expiry, but does not filter on `used_at IS NULL`. `MarkTicketAsUsedAsync` is a separate update. A validate-then-mark sequence is not an atomic one-time consume operation; concurrent requests can race. Replace this with a single database operation/transaction that conditionally consumes an unexpired, active, unused ticket and returns its bound identity only to the winning request.
5. **Guest identity/status handling.** `JoinController.GetAuthTicket` sets unauthenticated callers to user ID 0, then calls the ordinary game-ticket creation path. `SetClientStatus` also maps a missing/invalid identity claim to user ID 0 and accepts client-supplied status, place, job, and ticket values. Do not treat user ID 0 as an authenticated identity or trust this endpoint to establish a verified join. Design a separate scoped guest identity and verify the ticket and server-side join before recording presence.
6. **Arbiter control-plane protection.** Audit every route that starts, stops, or controls game servers. Require strong service authentication, restrict network reachability, and use POST for state-changing actions. Keep the Arbiter management API private.
7. **Heartbeat and cleanup behavior.** Verify the selected RCC build's native heartbeat behavior and ensure empty, failed, and crashed servers are eventually cleaned up without trusting unauthenticated client-supplied player counts.
8. **Network reachability.** Configure a real server address and the necessary game traffic path. A listening process or successful RCC health check is not proof that a client can join.

## Required compatibility evidence

Before changing the production join path, record:

- Exact RCC build/version and executable source/provenance.
- Exact client build/version intended for BLOCK16.
- Sanitized RCC startup and Arbiter logs.
- Full error context around reported errors 163 and 24, with tokens, keys, cookies, IPs, and personal data removed.
- Whether the RCC service responds to the control protocol used by this repository.
- Whether a server starts, advertises the expected reachable address, reports heartbeats, accepts a compatible client, and shuts down cleanly.

Do not guess the meaning of error codes 163 or 24 from the numbers alone.

## Implementation sequence

- [ ] Confirm repository reuse/licensing permission before copying additional upstream code or assets.
- [ ] Add automated tests for job/place validation and ticket one-time consumption.
- [ ] Replace validate-then-mark ticket handling with atomic one-time consumption.
- [ ] Separate guest identity/ticket handling from ordinary authenticated user tickets.
- [ ] Ensure client status and presence are established only from verified server-side join evidence.
- [ ] Protect Arbiter control routes and keep the RCC listener private.
- [ ] Verify heartbeat, player-count, failure, and cleanup behavior on a test host.
- [ ] Add a server-allocation adapter between BLOCK16 and the Windows-hosted Arbiter.
- [ ] Capture sanitized diagnostics for errors 163 and 24 when a test host exists, and identify the RCC/client version pair.
- [ ] Connect the website Play flow only after the compatibility and security gates pass.
- [ ] Run end-to-end tests with a compatible client before considering production rollout.

## External protocol background

Community documentation describes RCC protocol generations as differing across releases, including SOAP-based and JSON-based generations. Treat that as background only; the exact binary and client pair used by BLOCK16 is the source of truth. See [RCCService protocol overview](https://docs.mercs.dev/services/rcc/).

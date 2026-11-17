# BLOCK16 RCC integration plan

## Status

Planning and safety gate only. This document does not enable RCC, expose an Arbiter endpoint, change the live BLOCK16 website, or claim that any RCC build is compatible.

## Target architecture

- Keep the BLOCK16 website and its existing Cloudflare Worker deployment separate from RCC process hosting.
- Run the Arbiter and RCC processes on a dedicated, supported Windows host.
- Keep RCC's control interface private to the host or a tightly restricted private network. Do not publish the RCC SOAP/HTTP listener directly to the internet.
- Allow the public website/API to request game allocation only through a separately authenticated and narrowly scoped game-server API.
- Advertise a client-reachable game-server address; loopback addresses such as 127.0.0.1 are valid only for same-host testing.

## Findings that must be addressed before a live join test

1. **Protocol compatibility is unverified.** The current RCC client code uses SOAP methods such as `GetVersion` and `OpenJobEx`. The repository's JSON mode changes the job payload, but that does not by itself prove that the transport is compatible with a JSON-only RCC service. Test against the exact RCC build and matching client build before enabling allocation.
2. **Guest authentication response bug.** `PlaceLauncherController.HandleRequestGame` initializes `ticketToken` to an empty string and later uses `ticketToken ?? guestToken`. An empty string is not null, so the guest token fallback is never selected. Do not treat the existing generated guest string as a secure authentication credential; guest join needs a deliberate server-side ticket design.
3. **Job/place binding.** A caller-supplied job ID is currently accepted after a status check that reads the port but does not verify that the job belongs to the requested place. The allocation/join path must verify the job ID, place ID, and server state together before issuing join data.
4. **Ticket replay protection.** Review the game-ticket validation and consumption flow. A ticket must be bound to the intended user, place, and job, expire promptly, and be consumed atomically once. Do not rely on an in-memory flag that can be replayed or race.
5. **Arbiter control-plane protection.** Audit every route that starts, stops, or controls game servers. Require strong service authentication, restrict network reachability, and use POST for state-changing actions. Keep the Arbiter management API private.
6. **Heartbeat and cleanup behavior.** Verify the selected RCC build's native heartbeat behavior and ensure empty, failed, and crashed servers are eventually cleaned up without trusting unauthenticated client-supplied player counts.
7. **Network reachability.** Configure a real server address and the necessary game traffic path. A listening process or successful RCC health check is not proof that a client can join.

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
- [ ] Capture sanitized diagnostics for errors 163 and 24 and identify the RCC/client version pair.
- [ ] Add automated tests for job/place validation and ticket one-time consumption.
- [ ] Design guest tickets as server-issued, short-lived, scoped credentials.
- [ ] Protect Arbiter control routes and keep the RCC listener private.
- [ ] Verify heartbeat, player-count, failure, and cleanup behavior on a test host.
- [ ] Add a server-allocation adapter between BLOCK16 and the Windows-hosted Arbiter.
- [ ] Connect the website Play flow only after the compatibility and security gates pass.
- [ ] Run end-to-end tests with a compatible client before considering production rollout.

## External protocol background

Community documentation describes RCC protocol generations as differing across releases, including SOAP-based and JSON-based generations. Treat that as background only; the exact binary and client pair used by BLOCK16 is the source of truth. See [RCCService protocol overview](https://docs.mercs.dev/services/rcc/).

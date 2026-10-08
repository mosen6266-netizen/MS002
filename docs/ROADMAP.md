# V8 roadmap

## Stage 1 — foundation
- [x] Native WPF shell
- [x] Native dashboard with migrated account/group/script/job summary
- [x] Explicit desktop + Start Menu shortcut installer flow
- [x] Simplified-Chinese installer UI
- [x] Safe in-place upgrade handshake with active-send blocking
- [x] Versioned Signal runtime directory to avoid stale-runtime file locks
- [x] Independent per-user background engine
- [x] Named Pipe IPC
- [x] SQLite V8 schema
- [x] Durable dispatch-state skeleton
- [x] Atomic replay guard, account/group pre-send checks, restart quarantine
- [x] Guardian pause/check barrier before daemon restart
- [x] Fake-transport crash/cancellation/ambiguous-send regression tests
- [x] Standard Windows installer source
- [x] GitHub Actions Windows build
- [x] CI build proof / installer artifact proof

## Stage 2 — V7 data compatibility
- [x] Read V7 core schema without destructive mutation
- [x] Consistent SQLite backup before first metadata migration
- [x] Accounts + remarks metadata migration
- [x] Scripts + steps metadata migration
- [x] Groups + conservative job/recovery metadata migration
- [ ] Attachment binary migration/verification
- [x] Existing Cloudflare worker card activation/check with signed lease and DPAPI storage
- [ ] Lossless V7 device-id/credential migration and real-send license enforcement

## Stage 3 — Signal runtime
- [x] Bundle fixed Java 25 runtime
- [x] Bundle pinned signal-cli
- [x] JSON-RPC identity/health probe
- [x] Guardian healthy/busy/fault state machine
- [x] Reuse valid daemon / refuse to kill unknown port owner
- [x] Privacy-filtered rotating signal-cli diagnostics
- [x] Two-phase restart handshake with durable task engine (pause/check; no automatic resume)
- [x] Live account catalog sync preserving migrated remarks
- [x] Live group catalog sync
- [x] QR link flow
- [ ] Durable send transport (Signal sending remains disabled)

## Stage 4 — feature parity
- [x] Native account manager: custom remarks, enabled/disabled, persisted across relinks
- [x] Native editable script authoring with per-step insert/reorder, JSON import/export
- [x] Content-addressed local picture import and preview with SHA-256 checks
- [ ] Cross-device attachment bundle export/import and legacy V7 binary migration
- [x] Group selection by name and unique ID, saved across restart
- [ ] Typing/read logic
- [x] Multi-group rehearsal scheduler (SQLite-backed preview only, no actual Signal sends)
- [ ] Live Signal multi-group runner with durable send transport
- [x] Read-only recovery center, audit evidence copy, persistent manual pause
- [x] Manual pause/resume/stop for preview tasks (state persisted, blocked on ambiguous dispatch)
- [ ] Human adjudication and safe resume for real-send jobs (remains disabled)
- [x] Native critical alerts for engine/Signal/recovery status
- [ ] Existing Cloudflare licensing

## Stage 5 — production hardening
- [ ] Transactional updater/rollback
- [ ] Fault-injection tests
- [ ] crash/restart tests
- [ ] network/signal-cli failure tests
- [x] 20-group rehearsal cursor independence test
- [ ] Real Signal 20-group concurrency tests
- [ ] sleep/wake tests
- [ ] 24/72-hour soak tests
- [ ] optional code signing

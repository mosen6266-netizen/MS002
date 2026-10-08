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
- [ ] License compatibility

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
- [ ] Binary attachment import/export and preview
- [x] Group selection by name and unique ID, saved across restart
- [ ] Typing/read logic
- [ ] Multi-group runner
- [x] Read-only recovery center, audit evidence copy, persistent manual pause
- [ ] Safe resume/stop and adjudication workflow (disabled until runner is complete)
- [x] Native critical alerts for engine/Signal/recovery status
- [ ] Existing Cloudflare licensing

## Stage 5 — production hardening
- [ ] Transactional updater/rollback
- [ ] Fault-injection tests
- [ ] crash/restart tests
- [ ] network/signal-cli failure tests
- [ ] 20-group concurrency tests
- [ ] sleep/wake tests
- [ ] 24/72-hour soak tests
- [ ] optional code signing

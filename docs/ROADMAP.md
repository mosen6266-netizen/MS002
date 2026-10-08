# V8 roadmap

## Stage 1 — foundation
- [x] Native WPF shell
- [x] Native dashboard with migrated account/group/script/job summary
- [x] Explicit desktop + Start Menu shortcut installer flow
- [x] Independent per-user background engine
- [x] Named Pipe IPC
- [x] SQLite V8 schema
- [x] Durable dispatch-state skeleton
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
- [ ] Two-phase restart handshake with durable task engine
- [x] Live account catalog sync preserving migrated remarks
- [x] Live group catalog sync
- [x] QR link flow
- [ ] Durable send transport

## Stage 4 — feature parity
- [ ] Multi-account management UI
- [ ] Script editor/import/export
- [ ] Group selection
- [ ] Typing/read logic
- [ ] Multi-group runner
- [ ] Pause/resume/stop/recovery
- [ ] Native critical alerts
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

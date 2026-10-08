# V8 roadmap

## Stage 1 — foundation
- [x] Native WPF shell
- [x] Native dashboard with migrated account/group/script/job summary
- [x] Explicit desktop + Start Menu shortcut installer flow
- [x] Independent Windows Service
- [x] Named Pipe IPC
- [x] SQLite V8 schema
- [x] Durable dispatch-state skeleton
- [x] Standard Windows installer source
- [x] GitHub Actions Windows build
- [x] CI build proof / installer artifact proof

## Stage 2 — V7 data compatibility
- [ ] Read V7 schema without mutation
- [ ] Backup/rollback transaction
- [ ] Accounts + remarks
- [ ] Scripts + steps + attachments
- [ ] Groups + jobs + recovery states
- [ ] License compatibility

## Stage 3 — Signal runtime
- [ ] Bundle fixed Java runtime
- [ ] Bundle pinned signal-cli
- [ ] JSON-RPC transport
- [ ] Guardian state machine
- [ ] Two-phase restart handshake
- [ ] Account/group sync and QR link

## Stage 4 — feature parity
- [ ] Multi-account UI
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

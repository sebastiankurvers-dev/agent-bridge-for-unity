# Timeout Policy

<!-- Canonical timeout/retry rules for bridge operations. -->

## Default Timeouts

| Route Category | Default `timeoutMs` | Min | Max |
|---------------|--------------------:|----:|----:|
| Standard reads/writes | 5,000 | 250 | 120,000 |
| Script list / project index | 10,000 | 250 | 120,000 |
| Execute C# | 30,000 | 500 | 300,000 |
| Scene build / patch batch | 30,000 | 500 | 240,000 |
| Asset catalog generation | 60,000 | 500 | 300,000 |
| Scene profile extraction | 30,000 | 500 | 240,000 |

All endpoints accept an optional `?timeoutMs=<value>` query parameter (clamped to min/max).

## Retry Guidance

| Error Code | Retry? | Strategy |
|-----------|--------|----------|
| `TIMEOUT` | yes | Increase `timeoutMs`, or retry after compilation/play-mode transitions complete |
| `MAIN_THREAD_ERROR` (transient) | yes | Retry once after a short delay (~500ms) |
| `MAIN_THREAD_ERROR` (completion) | no | Fix the underlying issue (bad input, missing object, etc.) |
| `BAD_REQUEST` | no | Fix request parameters |
| `NOT_FOUND` | no | Check route spelling |
| `INTERNAL_ERROR` | no | Report bug; check `?debug=1` for stack trace |
| `CANCELED` | no | Request was canceled before execution; re-issue if still needed |

## When to Increase Timeout

- **Compilation in progress**: main thread is blocked; add 10-20s headroom
- **Play-mode transition**: editor is switching states; wait for `play_mode_changed` event
- **Large asset operations**: catalog generation, scene profile extraction, batch patches
- **First request after domain reload**: Unity may still be initializing

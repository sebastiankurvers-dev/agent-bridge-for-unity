# Error Envelope Contract

<!-- Canonical schema for all bridge error responses. -->

All error responses from `UnityAgentBridgeServer` use a standardized JSON envelope:

```json
{
  "success": false,
  "error": "Human-readable message",
  "code": "TIMEOUT",
  "route": "POST /snap",
  "retriable": true,
  "details": {}
}
```

## Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `success` | `bool` | yes | Always `false` for errors |
| `error` | `string` | yes | Human-readable error message |
| `code` | `string` | yes | Machine-parseable error code (see below) |
| `route` | `string` | yes | HTTP method + path that produced the error |
| `retriable` | `bool` | yes | Whether the caller should retry |
| `details` | `object` | no | Extra diagnostics; only populated when `?debug=1` query param is set |

## Error Codes

| Code | HTTP Status | Retriable | Meaning |
|------|-------------|-----------|---------|
| `TIMEOUT` | 504 | yes | Main-thread work item exceeded `timeoutMs` |
| `MAIN_THREAD_ERROR` | 500 | varies | Exception during main-thread execution (retriable for transient AggregateException, not for completion errors) |
| `BAD_REQUEST` | 400 | no | Invalid input (missing params, bad ID format, etc.) |
| `NOT_FOUND` | 404 | no | Route not matched |
| `INTERNAL_ERROR` | 500 | no | Unexpected server exception |
| `CANCELED` | 499 | no | Request canceled before execution |

## Debug Mode

Append `?debug=1` to any request to include `details` in error responses. Details may contain:

- Stack traces
- Queue diagnostics (pending queue size, work item ID)
- Timing info (queued duration, timeout value)
- Editor state (isCompiling, isPlaying, playModeTransitioning)

Without `?debug=1`, the `details` field is omitted to keep responses compact.

## Success Responses

Success responses are **unchanged** -- individual handlers format their own JSON with domain-specific fields. The error envelope only applies to error paths (bridge-level errors, not domain-level `success: false` from handlers).

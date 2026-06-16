# Observability & Instrumentation Audit — PoPunkouterSoftware

Date: 2026-06-16
Scope: API host (`PoPunkouterSoftware`), Infrastructure services, background jobs.
Method: code read of `Program.cs`, feature endpoints, background services, and config. No code changed.

---

## 1. Current state (what already exists)

The foundation is **above average** for a project this size. Concretely:

| Capability | Where | Verdict |
|---|---|---|
| Structured logging (Serilog, message templates not interpolation) | `Program.cs:60-87` | ✅ Good |
| Console + rolling file sinks (14-day retention) | `Program.cs:79-84` | ✅ Good |
| Correlation ID enrichment + `IHttpContextAccessor` | `Program.cs:57,77` | ✅ Good |
| Request logging with smart level selection (499 / aborts → Debug, 5xx/ex → Error) | `Program.cs:192-212` | ✅ Good |
| OpenTelemetry → Azure Monitor (App Insights), conditional on connection string | `Program.cs:92-97` | ✅ Good |
| Global exception handler → RFC 7807 ProblemDetails, ties `traceId` to `Activity.Current` | `GlobalExceptionHandler.cs:19-55` | ✅ Good |
| Liveness + readiness style endpoints (`/healthz`, `/health`) | `HealthEndpoints.cs` | ✅ Good (hand-rolled) |
| Config masking in diagnostics output | `HealthEndpoints.MaskValue`, `DiagEndpoints` | ✅ Good |
| Startup config health warnings | `Program.cs:184-190` | ✅ Good |

The trace/log pillars are largely covered. The **metrics pillar and the alerting layer are the real gaps.**

---

## 2. On-call questions (the missing foundation)

Per the skill: telemetry without a question is noise. These are the questions an on-call engineer will actually ask, by feature. Each one is then mapped to a signal in §3.

**Azure report refresh** (`POST /api/diag/refresh`, `AzureReportService`)
1. Is a refresh succeeding, and how long does the full scan take (p50/p95)?
2. When a refresh fails, why — timeout, ARM auth, or a specific scan step?
3. How often does a refresh get cancelled or collide with one already running (409)?

**Service pinger** (`ServicePingerService`, background job)
4. Which monitored Azure services are currently unreachable/degraded, and for how long?
5. Is the pinger sweep itself running on schedule, or has the loop died silently?
6. Is any service's response time trending up (cold-start / degradation)?

**Azure OpenAI integration** (`AzureOpenAiClient`)
7. What fraction of AI calls fail, and with what status (429 throttle vs 5xx vs timeout)?
8. Is Azure OpenAI slower than usual (p95 latency of the call)?

**GitHub integration** (`GitHubEndpoints`)
9. Are we being rate-limited by GitHub, and how close to the limit are we?

**Platform / RED across all endpoints**
10. What is the error rate and p99 latency per route, right now vs baseline?

> If a question below has no signal, on-call is guessing. Today, **4, 5, 6, 7, 8, 9 have no metric** — only scattered logs.

---

## 3. Signal mapping (question → signal)

| # | Question | Signal today | Gap |
|---|---|---|---|
| 1 | Refresh duration | Log: `"Azure report refreshed in {ElapsedMs}ms"` (`DiagEndpoints.cs:96`) — structured ✅ | No **metric/histogram** → can't chart p95 or alert |
| 2 | Refresh failure cause | Log: `LogError(ex, ...)` (`DiagEndpoints.cs:106`) ✅ | No success/failure **counter** |
| 3 | Refresh collisions | 409 returned, **not logged** (`DiagEndpoints.cs:56`) | No signal at all |
| 4 | Service reachability | `PingResult` cached, `LogDebug` per service (`ServicePingerService.cs:95`) — Debug is off in prod | No **gauge/metric**; not alertable |
| 5 | Pinger loop alive | `LogInformation` per sweep (`:107`) ✅ | No **heartbeat metric**; silent death undetectable |
| 6 | Response-time trend | In `PingResult.ResponseTimeMs`, cached only | Not emitted as **histogram** |
| 7 | AI failure rate | `LogWarning` on non-2xx (`AzureOpenAiClient.cs:52`) ✅ | No **counter** by status class |
| 8 | AI latency | none | No **histogram** |
| 9 | GitHub rate limit | none (headers not inspected) | No signal |
| 10 | RED per route | Azure Monitor auto-collects ASP.NET request duration/count ✅ | Adequate; verify dashboards exist |

**Conclusion:** logs are healthy; the work is to add a small set of **custom metrics** (RED on external dependencies + 2 job heartbeats) and to wire **symptom-based alerts** on top of them.

---

## 4. Prioritized gaps

### P1 — App Insights connection string committed to source
`appsettings.json:36` contains the full connection string including `InstrumentationKey`.
- **Severity: moderate** (an App Insights connection string only grants *write* access to send telemetry into your resource — it is not a read credential or a high-value secret). But it is still configuration that does not belong in git, it is environment-specific, and it trains the habit the skill warns against.
- **Fix:** remove from `appsettings.json`; source it from Key Vault (already wired at `Program.cs:39-42`) under `ApplicationInsights:ConnectionString`, or from `APPLICATIONINSIGHTS_CONNECTION_STRING` env var (which `UseAzureMonitor` reads automatically). Keep a `""` placeholder so local dev stays quiet.

### P2 — No custom metrics on external dependencies or jobs
The single highest-value gap. `AddOpenTelemetry()` (`Program.cs:93`) registers no custom `Meter`/`ActivitySource`. Add a `WithMetrics` registration and one `Meter` per concern:
- `azure_openai_calls_total{status_class}` + `azure_openai_call_duration` (histogram)
- `github_api_calls_total{status_class}` + `github_rate_limit_remaining` (gauge from response header)
- `pinger_service_up{service}` (gauge 0/1) + `pinger_response_time_ms{service}` (histogram) + `pinger_sweep_timestamp` (heartbeat)
- `azure_refresh_total{outcome}` (success/failed/cancelled/collision) + `azure_refresh_duration` (histogram)

**Cardinality check:** the only proposed label with any breadth is `{service}` — bounded by the monitored-service list (small, fixed). `status_class` is `2xx/4xx/5xx`. No user IDs, URLs, or messages as labels. Safe.

### P3 — Background-job silent-death blindness
`ServicePingerService.ExecuteAsync` swallows `OperationCanceledException` (correct) but any other unhandled throw inside the loop would terminate the `BackgroundService` with only framework logging. There is no heartbeat metric, so a dead pinger looks identical to a healthy-but-quiet one. Emit `pinger_sweep_timestamp` each sweep and alert on staleness (Q5).

### P4 — Refresh collision (409) and cancel paths unobserved
`DiagEndpoints.cs:56` returns 409 with no log/metric; cancellations log at Warning but aren't counted. Add the `azure_refresh_total{outcome}` counter so collisions/cancellations are quantifiable (Q3).

### P5 — No alerts-as-code
App Insights is connected but the repo has no alert definitions (only `infra/*.ps1` for identity/KV). Symptom-based alerts (§5) should live in source next to the app.

### P6 — Two parallel correlation identities (low)
Serilog's `WithCorrelationId` (`Program.cs:77`) produces a `CorrelationId` separate from the OpenTelemetry `Activity.TraceId` used by `GlobalExceptionHandler` and Azure Monitor. Logs carry one ID, distributed traces another. Not broken, but cross-referencing a log line to its trace in App Insights is harder than it should be. Consider standardizing on the OTel `TraceId` (Serilog can enrich from `Activity.Current`) so one ID joins logs and traces.

### P7 — Health checks are hand-rolled (low)
`/health` works but bypasses ASP.NET Core's `AddHealthChecks()` pipeline, so it can't feed the standard `IHealthCheck` publisher into App Insights availability or be reused by container/orchestrator probes uniformly. Acceptable as-is; note for future consolidation.

---

## 5. Alerting plan (symptom-based, page vs ticket)

Two severities only. Every alert lists the question it answers, a threshold with justification, and a one-line runbook seed.

| Sev | Alert | Condition (symptom) | Answers | Runbook seed |
|---|---|---|---|---|
| **Page** | API error rate | 5xx rate > 2% over 5 min | Q10 | Check App Insights Failures blade → group by operation; recent deploy? |
| **Page** | API latency | p99 request duration > 3s over 10 min | Q10 | Failures/Performance blade → slowest dependency; ARM throttling? |
| **Page** | Monitored service down | `pinger_service_up{service} == 0` for > 15 min (3 sweeps) | Q4 | Open `/api/pinger/status`; confirm real outage vs probe issue; the service URL itself |
| **Page** | Pinger heartbeat stale | `now − pinger_sweep_timestamp > 3 × interval` (>30 min) | Q5 | Pinger loop likely died; check host logs for unhandled ex; restart host |
| **Ticket** | AI degraded | `azure_openai_calls_total{status_class="5xx"}` rate > 5% over 30 min, OR p95 duration > 20s | Q7, Q8 | Azure OpenAI resource health; 429 → raise quota; 5xx → provider status |
| **Ticket** | GitHub near rate limit | `github_rate_limit_remaining < 100` | Q9 | PAT shared too widely; back off polling; check `GitHub:PersonalAccessToken` scope |
| **Ticket** | Refresh failing | `azure_refresh_total{outcome="failed"}` ≥ 2 in 1 h | Q2 | Logs for the failing scan step; `az login` / managed-identity token; ARM throttle |

**Explicitly NOT alerts** (dashboard only, per skill): CPU%, memory, a single pod restart, refresh collisions (self-healing 409). These are causes, not user-felt symptoms.

Each Page alert must link to a runbook (even three lines) and be test-fired once by temporarily lowering its threshold before this work is called done.

---

## 6. Verification plan (prove the telemetry, don't assume it)

After implementing P2–P5, before closing the work:
- [ ] Force an Azure OpenAI 4xx in staging → find the `azure_openai_calls_total{status_class="4xx"}` increment **and** the correlated log line by `TraceId`.
- [ ] Disable one pinger target → confirm `pinger_service_up{service}` flips to 0 and the Page alert fires to the right channel.
- [ ] Kill/pause the pinger → confirm heartbeat-stale alert fires within 30 min.
- [ ] Trigger a refresh → confirm `azure_refresh_duration` histogram appears with a sane value and one trace spans the scan end-to-end with no broken spans.
- [ ] Grep actual log output for the App Insights key and any secret → confirm none present.
- [ ] Confirm every new metric series' labels are bounded (no unexpected high-cardinality series in the App Insights metrics namespace).

---

## 7. Suggested implementation order

1. **P1** — pull the connection string out of `appsettings.json` (5-min, highest-habit-value).
2. **P2 + P3** — add a `Telemetry` static `Meter` + `WithMetrics()` in `Program.cs`; instrument `AzureOpenAiClient`, `ServicePingerService`, and the refresh path. This unlocks Q4–Q8.
3. **P4** — refresh outcome counter (cheap once the meter exists).
4. **P5** — author the §5 alerts as code (Bicep/`az monitor` script under `infra/`), test-fire each.
5. **P6/P7** — optional consolidation, schedule as follow-up tickets.

Items 1–4 are a focused change set touching ~4 files. Say the word and I'll implement them incrementally (one concern per commit), with the verification steps from §6 run against each.

# Optional model setup

The default `MSGUIDE_GUIDANCE_PROVIDER=demo` is deterministic and makes **no remote model calls**. It supports the built-in MSGuide Demo only. A real OpenAI-compatible transport is implemented in [src/model_provider.py](../src/model_provider.py), but live-model desktop grounding has not been verified.

## Configure only an approved provider

Use synthetic/public data only. Before enabling remote processing, choose an approved provider, review its data retention/access/cost policies, and obtain the exact HTTPS inference URL, model identifier, and credential through your approved process. There are **no URL, model-name, or API-key defaults**.

Set these in the **current PowerShell 7 process environment before launching**; do not persist them in a profile, source file, or user/machine environment:

| Variable | Required value / meaning |
| --- | --- |
| `MSGUIDE_GUIDANCE_PROVIDER` | `openai-compatible` to select the transport. |
| `MSGUIDE_ALLOW_REMOTE_MODEL` | `true`, explicitly permitting remote processing. Required even if the approved destination is local HTTPS. |
| `MSGUIDE_MODEL_URL` | Complete approved HTTPS POST endpoint with a non-root path; no credentials in the URL, fragments, or whitespace. The client does not append a route. |
| `MSGUIDE_MODEL_NAME` | Explicit provider model/deployment identifier, at most 256 characters. |
| `MSGUIDE_MODEL_API_KEY` | Approved credential, supplied locally; never paste it into chat. |
| `MSGUIDE_MODEL_AUTH_HEADER` | `bearer` (default, Authorization bearer header) or `api-key`. |

For credential entry, use an approved local secret mechanism or PowerShell `Read-Host -AsSecureString`, converting only locally for the child-process environment. Do not type a literal secret into command history or print it. An environment variable is plaintext process state, not a secret vault; secure prompting does not change that. Clear it from the parent process when finished. No secret-entry script or example credential is supplied here.

`MSGUIDE_MODE=demo` is a **separate local-user security setting**, not the choice of guidance engine. The launcher sets it for both children; non-demo security modes are rejected. A model configuration does not enable enterprise identity or authorize internal data.

Run [scripts/Start-MSGuide.ps1](../scripts/Start-MSGuide.ps1) normally after configuration. It passes model credentials to Python and strips `MSGUIDE_MODEL_API_KEY` from the desktop child. A healthy backend then reports `mode: model`, and the desktop labels responses **MODEL**. Invalid/incomplete configuration prevents startup; there is no silent fallback. `-IntegrationTest` overrides provider selection to `demo`, so it cannot validate a live model.

## Provider compatibility

The endpoint must accept `model`, `messages`, `stream: false`, `response_format:
{type: json_object}`, and `max_tokens` of 8000 for plans or 1200 for legacy
single-step requests. Approved images use a PNG data URL. The exact configured
URL is used, without redirects or an appended route.

The response must have one stopped assistant choice containing validated JSON.
For `planRequested: true`, return `instruction`, `status: next_step`, and `plan`
matching the supplied `planSchema`; a missing plan fails, never silently
degrades to one click. Plans describe up to 32 steps to a resource/information/
permission/observation/unsupported/plan-limit boundary or a completion suggestion.
Each action selects an observed `targetIndex` or an exact deferred intent; the
server derives observed logical IDs and previous-value constraints. Deferred
writes can replace only an empty writable non-password field. See the full
[plan contract](API.md#structured-plan-segments).

Legacy single-step requests still support `targetIndex`, `remainingWork`, `value`
and `scrollDirection`. Legacy `completed` becomes `completion_candidate`, never
verified generic success. Unknown fields, tools, duplicate keys, invented IDs,
malformed later steps, refusals and oversized output fail closed. Responses are
bounded to 128 KiB; content is at most 64 KiB for plans or 16 KiB for legacy output.

HTTP redirects, automatic retries, and environment proxies are disabled. Plans
have a 50-second whole-call cap (48-second socket timeout); legacy calls retain
10/8-second caps. Both reserve ten seconds of remaining evidence freshness and
use a three-second connection timeout. Failures expose no response bodies or
credentials. The provider does not execute tools or handle authentication flows.

## What is shared

After **Capture / review → consent → Send**, the remote provider receives the prompt and reviewed observation metadata, including application/window identifiers, timestamp, UIA names, element boxes, and dimensions. Text-only approval still transmits that metadata remotely in model mode.

Manual screenshot pixels are sent **only if opted in for that snapshot**. The
explicit `-Copilot` grant covers initial planning and explicitly reviewed
replanning; routine steps inside a retained segment use local UIA observations
without another inference call/upload. Camera verification remains separate.
[src/images.py](../src/images.py) validates/re-encodes PNGs without metadata, not
pixel redaction or OCR. Discard sensitive evidence; already sent content cannot
be recalled, and provider retention is outside this application's control.

To return to local-only guidance, stop the desktop/launcher, set `MSGUIDE_GUIDANCE_PROVIDER=demo` (or remove it), remove the remote-approval and model credential variables from the current process, and restart. Verify the **DEMO** label. See [validation](VALIDATION.md) for the distinction between mocked provider tests and unverified live behavior.

## GitHub Copilot SDK provider (integration seam)

`src/copilot_provider.py` provides an optional `CopilotProvider` with the same
`async provider(prompt, Observation, *, task=None, plan=False) -> GuidanceResult` callable seam. It is
selected by the launcher's `-Copilot` switch; deterministic demo guidance
remains the default without that switch. `OpenAICompatibleProvider` remains
available.

The checked-in locks target Windows x64 CPython 3.11.9 (including x64 Python
on ARM64 Windows). Setup uses portable hash-pinned requirements and requires a
GitHub Copilot entitlement unless the SDK is configured separately for BYOK:

```powershell
python -m venv venv
.\venv\Scripts\python -m pip install --require-hashes --only-binary=:all: -r requirements-dev.lock.txt
.\venv\Scripts\python -m copilot download-runtime
```

Use `requirements.lock.txt` instead of `requirements-dev.lock.txt` for runtime-only
installation with the same hash/binary flags. The locks contain the tested project
closure and wheel hashes, not unrelated packages or workstation-specific mirror
URLs. Normal installation needs no experimental lock support. Direct intent
remains in `requirements.txt` / `requirements-dev.txt`; intentional regeneration
uses pip 26.2.1 through `scripts\lock_python_dependencies.py`. Keep TLS validation enabled.

The pinned `github-copilot-sdk==1.0.13` wheel requires `pydantic>=2`,
`httpx>=0.24`, and `python-dateutil>=2.9.0.post0`; the existing pinned Pydantic
and HTTPX versions satisfy those bounds. Runtime download is also performed
automatically on first managed use, but pre-provisioning avoids first-step
latency.

To reuse an already installed and authenticated Copilot CLI without touching
the SDK runtime download cache, pass its absolute executable path:

```python
from pathlib import Path

config = CopilotProviderConfig(
    model="gpt-6-astra",
    reasoning_effort="xhigh",
    context_tier="long_context",
    base_directory=Path(r"C:\ProgramData\MSGuide\copilot"),
    cli_path=Path(r"C:\path\to\copilot.exe"),
)
```

Alternatively, set `COPILOT_CLI_PATH` in the launching process. An explicit
`cli_path` takes precedence. The path must be absolute and identify an existing
file; resolve it on PowerShell with `(Get-Command copilot).Source`. The provider
passes it through `RuntimeConnection.for_stdio(path=...)`, which bypasses the
bundled runtime download/install path and avoids concurrent cache extraction.
Do not point it at `agency copilot`; Agency integration is the separately
bounded MCP server described below.

The launcher pins GPT-6 Astra with `low` reasoning and the default context
tier to keep foreground guidance interactive. On the validated Copilot
entitlement, the SDK reported a 1,178,000-token context window. GPT-5.6 Sol also
remains available and supports `max`, but its reported context window is
1,050,000 tokens. Override `MSGUIDE_COPILOT_MODEL`,
`MSGUIDE_COPILOT_REASONING_EFFORT`, and `MSGUIDE_COPILOT_CONTEXT_TIER` only with
values supported by the selected model.

### Planning latency comparison

Run the synthetic comparison using the existing authenticated Copilot executable:

```powershell
$env:COPILOT_CLI_PATH = (Get-Command copilot.exe -CommandType Application).Source
.\venv\Scripts\python -m scripts.benchmark_planning --models gpt-6-astra gpt-5.4-mini --repeats 3 --output "$env:TEMP\msguide-planning-benchmark.json"
```

This makes at most 12 sequential planning requests with public synthetic controls
and an optional generated image; it never captures the desktop or executes actions.
It checks the ordered actions, exact input, grounding and resource boundary.
The report separates planning from runtime startup/cleanup and exits nonzero if
any sample fails. Failed requests are not counted as successful speedups.

The initial three-sample-per-mode comparison with SDK 1.0.13 found:

| Model | UIA-only median | With generated image median | Correct plans |
| --- | --- | --- | --- |
| `gpt-6-astra` | 19.44 s | 18.25 s | 6/6 |
| `gpt-5.4-mini` | 13.83 s (rejected) | 14.61 s (rejected) | 0/6 |

All mini requests ended with `invalid_result`; keep Astra rather than trade
correctness for rejected responses. One small workflow and three samples per
mode do not establish an overall speedup or a UIA-only latency advantage.
`-Copilot -UiaOnly` remains an explicit option for tasks fully described by
accessible controls/text; omit it for visual tasks. Final-page completion and
real desktop capture/execution latency are outside this benchmark.

### Provider lifecycle

An integrator must:

1. Build a `CopilotProviderConfig` with an explicit model and an absolute SDK
   base directory. The default is the signed-in user's `~/.copilot` directory
   so the stdio runtime can reuse the user's existing Copilot authentication.
   Set `MSGUIDE_COPILOT_HOME` only when that alternate directory is already
   authenticated.
2. Supply a context resolver that reads only deterministic scenario state and
   returns `ApprovedGuidanceContext`: the current observation ID, server-owned
   step ID, approved UIA element indexes/target IDs, and pre-approved citation
   IDs. Do not derive scenario state or completion from model output.
3. Create one provider at application startup and `await provider.start()`.
4. Inject that provider through the existing callable seam.
5. `await provider.close()` during application shutdown.
6. Catch `CopilotProviderFailure` and invoke the caller-owned deterministic
   fallback explicitly. The provider never falls back silently.

Each plan/replan call uses an isolated SDK session and the persistent `CopilotClient`
runtime; routine locally grounded steps do not create new sessions. The task ID,
step, last 16 action outcomes, retained plan/cursor, original request and bounded remaining-work/user-input context are passed
explicitly as **untrusted continuation data**. Old targets are never cached or
reused for execution. The model-facing allowlist references the observation's
element indexes instead of repeating UIA labels/states. Complete control evidence
and useful history remain present once. Non-actionable elements remain descriptive
context, not execution targets.

Session storage, memory, and infinite sessions remain disabled. The replaced
system prompt, explicit tool allowlist, and deny-by-default permission handler
do not grant shell, filesystem, editor, or arbitrary tool access. Only the
local `submit_guidance` tool returns guidance. It requires current
`observationId`/`stepId`, an explicit status (`next_step`, `needs_input`,
`blocked`, or `completion_candidate`), instruction, allowlisted target/citation
IDs, and optional bounded remaining work or semantic input for legacy requests.
Plan requests instead require a whole structured `plan` with root `next_step`
and no legacy target. Future semantic intents are not opaque target authority;
fresh unique local rebinding is mandatory. One correction is permitted; absent/invalid
submissions now return an explicit sanitized failure, not a success-shaped
clarification. Model completion is only a suggestion for user/local review.

The 60-second evidence TTL is unchanged. Whole-call limits are the smaller of
the configured cap and remaining freshness: **50 seconds / 5 seconds reserved**
for Copilot, **52 / 3** for the API, and **54 / 1** for the desktop. Lock waits,
prior cleanup, session creation, and attachment preparation consume that same
budget; `send_and_wait(timeout=remaining)` is explicit rather than the SDK's
60-second default. Post-inference freshness and exact-target checks remain.

HTTP disconnect, cancellation, and timeout cancel the owned provider turn and
invalidate its callback. Since cancelling `send_and_wait` alone does not stop
agent work, cleanup explicitly calls **abort**, then **disconnect**. Accepted
guidance returns without waiting for teardown; at most one retiring session
exists, and the next call drains it before creating another. Abort and detach
each use the configured shutdown deadline (five seconds by default), not a
one-second acknowledgement allowance that can reject a healthy runtime.
Both stages remain bounded and their elapsed times are recorded.
Unconfirmed cleanup stops the owned runtime (up to the
configured shutdown timeout, five seconds by default) and blocks further
requests until restart; cancelled session creation without a returned ID also
stops that runtime. Remote termination cannot be guaranteed if the SDK/runtime
itself fails to acknowledge shutdown. No unbounded cleanup queue is created.

The WPF client, not the model or `/v1/jobs`, executes actions. Only **Fix it for
me** plus the launch grant can auto-execute; Guide mode never does. Runs no longer
pause after eight actions or two minutes: every queued action is freshly checked
and observed. After progress, `plan_limit` and `observation` boundaries request a
new plan automatically after refreshing the selected window. Normal page/resource
changes within that window also recapture and replan without repeated approval,
using only complete identified evidence; old-page targets are discarded.
Empty plans, unknown page identity and actual input/permission/new-window
boundaries still stop; per-operation deadlines and the 10,000-decision protocol
ceiling remain. Boundary reasons/needed input are visible in
the compact prompt and Details; unknown/cancelled queued actions cannot resume.
`set_value` and `scroll` require writable/non-password bounded
ValuePattern or ScrollPattern evidence and explicit inputs. No coordinate,
keyboard, shell, or unknown-outcome retry fallback exists.

Screenshot bytes are attached only when approved `Observation.imageBase64` is
present. The desktop sends an initial, same-window automatic refresh, or explicitly reviewed replanning image,
then uses fresh local UIA-only steps/checks instead of another inference per action.
Execution first presents the grounded target in the foreground with the Windows
marker; it never falls back to background input. A supported Edge/Chrome page can
be scoped by a unique canonical browser-chrome display address combined with
the native active document identity, not by a URL claimed in web content.
This handles protocol-prefix elision and ignores auxiliary document wrappers;
the model only receives controls inside that verified page. Generic file/site document surfaces without proven resource identity stop for
handoff rather than speculative multi-step navigation. No screenshot or task
history file is created. This removes repeated image work and foreground
teardown latency, not remote inference or per-plan session creation latency.
Provider startup is bounded to 30 seconds by default; the launcher allows 60
seconds for Python/imports plus readiness and logs sanitized startup stages/errors.
Runtime ownership begins with the startup attempt, not only after readiness.
Startup failure, timeout and cancellation trigger bounded SDK shutdown, and API
lifespan cleanup also covers failed startup. An unconfirmed shutdown retains
ownership and blocks provider reuse; explicit close can join the pending stop or
retry after a failed stop has finished, without queuing overlapping stop workers.

By default the provider reuses the user's existing `copilot login` credential
through the system credential store. It checks authentication once during
startup, within the startup deadline; missing/unavailable authentication fails
startup explicitly before any screen is shared. No credentials are read or
copied by MSGuide.

Embedded sessions explicitly disable automatic configuration, skills, hooks,
and custom-instruction discovery, host Git operations and remote custom-agent
discovery. Because the pinned runtime can still load registered MCP servers
with configuration discovery disabled, each turn first reads the SDK's server
inventory and passes all inherited server names in the **session-only disabled
list**, except an explicitly configured legacy Learn server. Inventory failure
rejects the request before an unisolated session can be created. The normal
CLI's personal MCP/plugin setup is not part of a screen-planning request. This isolation is separate from the
The session-only disabled list also includes `github-mcp-server`, because that
built-in server is not returned by the configuration inventory. Disabling its
tool connection does not disable the cached GitHub login used for model access. This isolation is separate from the
existing keychain login: using `mode="empty"` without an explicit token would
disable keychain access in the pinned SDK. Any MCP OAuth request that still
reaches the host is cancelled rather than opening an interactive sign-in flow.
Only explicitly configured legacy Learn integration is allowed; this does not
modify or disable integrations in the user's regular CLI.

The provider replaces the system prompt, exposes only `submit_guidance`, and
disables session storage, memory, and infinite sessions.
If `COPILOT_GITHUB_TOKEN` is explicitly supplied, the provider uses
SDK `mode="empty"` instead; the launcher removes that token from the desktop
process environment, and MSGuide never writes it to disk.

### Optional Agency Microsoft Learn MCP

Structured plan requests do not start external retrieval servers or approve
retrieval permissions, even if the legacy retrieval option is enabled. They
return a resource boundary instead. For legacy single-step requests, set
`AgencyMicrosoftLearnConfig(enabled=True)` only on machines where `agency`
is installed and its Microsoft integration is approved. The SDK session starts
the local stdio server as:

```text
agency mcp msft-learn
```

The provider defaults to the exact read-only
`microsoft_docs_search` tool. The additionally recognized read-only tools are
`microsoft_code_sample_search` and `microsoft_docs_fetch`; opt into a subset
explicitly. These names and their read-only annotations were verified through
MCP `tools/list` on Agency 2026.9.15.5. MCP is disabled by default, startup is
bounded, and failures surface as `CopilotProviderFailure`. For the demo,
pre-bundle approved Microsoft Learn citations in `ApprovedGuidanceContext`
instead of depending on MCP startup, authentication, or network availability.

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

The endpoint must accept the chat-completions-style fields `model`, `messages`, `stream: false`, `max_tokens: 1200`, and `response_format: {type: json_object}`. If images are approved, it must support an image content part containing a PNG data URL. The request posts to the exact configured URL.

The response must have exactly one stopped assistant choice containing a JSON object with `instruction`, `status`, and optional `targetIndex`, `remainingWork`, `value`, and `scrollDirection`. Statuses include `next_step`, `clarification`/`needs_input`, `blocked`, and `completion_candidate`; legacy `completed` is downgraded to `completion_candidate`, never treated as a verified generic goal. Unknown fields, tools, URLs/citations, duplicate JSON keys, invalid indexes, refusals, and oversized responses fail closed. Action targets must expose the same supported capability in fresh UIA evidence. `value` is required only for `set_value` (full-field replacement, 0-1000 characters); `scrollDirection` is required only for a `scroll` target and must be in its allowlist.

HTTP redirects, automatic retries, and environment proxies are disabled. The OpenAI-compatible call is bounded to ten seconds overall or the remaining evidence lifetime minus ten seconds, whichever is shorter; socket timeouts are eight seconds, with three seconds for connecting. Failures return generic errors without exposing provider response bodies or credentials. The client does not implement provider-specific authentication flows or tool execution.

## What is shared

After **Capture / review → consent → Send**, the remote provider receives the prompt and reviewed observation metadata, including application/window identifiers, timestamp, UIA names, element boxes, and dimensions. Text-only approval still transmits that metadata remotely in model mode.

Manual screenshot pixels are sent **only if opted in for that snapshot**. The explicit `-Copilot` launch grant also covers the first automatic task image; later task steps are fresh UIA-only. [src/images.py](../src/images.py) validates size/type/dimensions with Pillow and re-encodes pixel-only PNGs, dropping metadata. It does not redact pixels or perform OCR. Inspect both image and text; discard any sensitive content. Already sent content cannot be recalled, and provider retention is outside this application's control.

To return to local-only guidance, stop the desktop/launcher, set `MSGUIDE_GUIDANCE_PROVIDER=demo` (or remove it), remove the remote-approval and model credential variables from the current process, and restart. Verify the **DEMO** label. See [validation](VALIDATION.md) for the distinction between mocked provider tests and unverified live behavior.

## GitHub Copilot SDK provider (integration seam)

`src/copilot_provider.py` provides an optional `CopilotProvider` with the same
`async provider(prompt, Observation, *, task=None) -> GuidanceResult` callable seam. It is
selected by the launcher's `-Copilot` switch; deterministic demo guidance
remains the default without that switch. `OpenAICompatibleProvider` remains
available.

Clean-machine setup requires Python 3.11+, a GitHub Copilot entitlement (unless
the SDK is configured separately for BYOK), and:

```powershell
python -m pip install -r requirements.txt -r requirements-dev.txt
python -m copilot download-runtime
```

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

Each call still uses an isolated SDK session and the persistent `CopilotClient`
runtime. The task ID, monotonically increasing step, last 16 action outcomes,
original request, and bounded remaining-work/user-input context are passed
explicitly as **untrusted continuation data**. Old targets are never cached or
reused for execution. Non-actionable elements remain useful observation context
but cannot appear in the execution allowlist.

Session storage, memory, and infinite sessions remain disabled. The replaced
system prompt, explicit tool allowlist, and deny-by-default permission handler
do not grant shell, filesystem, editor, or arbitrary tool access. Only the
local `submit_guidance` tool returns guidance. It requires current
`observationId`/`stepId`, an explicit status (`next_step`, `needs_input`,
`blocked`, or `completion_candidate`), instruction, allowlisted target/citation
IDs, and optional bounded remaining work or semantic input. `next_step` needs
a target; other statuses forbid one. The action comes from current evidence,
not a model-selected command. One correction is permitted; absent/invalid
submissions now return an explicit sanitized failure, not a success-shaped
clarification. Model completion is only a suggestion for user/local review.

The 60-second evidence TTL is unchanged. Whole-call limits are the smaller of
the configured cap and remaining freshness: **50 seconds / 10 seconds reserved**
for Copilot, **52 / 8** for the API, and **54 / 6** for the desktop. Lock waits,
prior cleanup, session creation, and attachment preparation consume that same
budget; `send_and_wait(timeout=remaining)` is explicit rather than the SDK's
60-second default. Post-inference freshness and exact-target checks remain.

HTTP disconnect, cancellation, and timeout cancel the owned provider turn and
invalidate its callback. Since cancelling `send_and_wait` alone does not stop
agent work, cleanup explicitly calls **abort**, then **disconnect**. Accepted
guidance returns without waiting for teardown; at most one retiring session
exists, and the next call drains it before creating another. Abort/detach share
a two-second ceiling. Unconfirmed cleanup stops the owned runtime (up to the
configured shutdown timeout, five seconds by default) and blocks further
requests until restart; cancelled session creation without a returned ID also
stops that runtime. Remote termination cannot be guaranteed if the SDK/runtime
itself fails to acknowledge shutdown. No unbounded cleanup queue is created.

The WPF client, not the model or `/v1/jobs`, executes actions. Only **Fix it for
me** plus the launch grant can auto-execute; Guide mode never does. Each batch
has at most eight actions and post-action verification, including its final
action. `set_value` and `scroll` require writable/non-password bounded
ValuePattern or ScrollPattern evidence and explicit inputs. No coordinate,
keyboard, shell, or unknown-outcome retry fallback exists.

Screenshot bytes are attached only when approved `Observation.imageBase64` is
present. The desktop sends the first automatic task image, then fresh UIA-only
steps/checks rather than another full image at every step. No screenshot or task
history file is created. This removes repeated image work and foreground
teardown latency, not remote inference or per-call session creation latency.

By default the provider reuses the user's existing `copilot login` credential
through the system credential store. It replaces the system prompt, exposes
only `submit_guidance`, and disables session storage, memory, and infinite
sessions. If `COPILOT_GITHUB_TOKEN` is explicitly supplied, the provider uses
SDK `mode="empty"` instead; the launcher removes that token from the desktop
process environment, and MSGuide never writes it to disk.

### Optional Agency Microsoft Learn MCP

Set `AgencyMicrosoftLearnConfig(enabled=True)` only on machines where `agency`
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

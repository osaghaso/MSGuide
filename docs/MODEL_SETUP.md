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

The response must have exactly one stopped assistant choice containing a JSON object with `instruction`, `status`, and optional `targetIndex`. Unknown model-output fields, tools, URLs/citations, duplicate JSON keys, invalid coordinates/indexes, refusal responses, and oversized responses fail closed. Targets come only from supplied UIA candidates with confidence at least 0.8. Without a reliable UIA target, guidance may be non-targeted; the model cannot draw an invented box.

HTTP redirects, automatic retries, and environment proxies are disabled. The OpenAI-compatible call is bounded to ten seconds overall; socket timeouts are eight seconds, with three seconds for connecting. Failures return generic errors without exposing provider response bodies or credentials. The client does not implement provider-specific authentication flows or tool execution.

## What is shared

After **Capture / review → consent → Send**, the remote provider receives the prompt and reviewed observation metadata, including application/window identifiers, timestamp, UIA names, element boxes, and dimensions. Text-only approval still transmits that metadata remotely in model mode.

Screenshot pixels are sent **only if separately opted in for that snapshot**. [src/images.py](../src/images.py) validates size/type/dimensions with Pillow and re-encodes pixel-only PNGs, dropping metadata. It does not redact pixels or perform OCR. Inspect both image and text; discard any sensitive content. Already sent content cannot be recalled, and provider retention is outside this application's control.

To return to local-only guidance, stop the desktop/launcher, set `MSGUIDE_GUIDANCE_PROVIDER=demo` (or remove it), remove the remote-approval and model credential variables from the current process, and restart. Verify the **DEMO** label. See [validation](VALIDATION.md) for the distinction between mocked provider tests and unverified live behavior.

## GitHub Copilot SDK provider (integration seam)

`src/copilot_provider.py` provides an optional `CopilotProvider` with the same
`async provider(prompt, Observation) -> GuidanceResult` callable seam. It is
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

The launcher pins GPT-6 Astra with `xhigh`, the highest reasoning effort that
model currently advertises, and `long_context`. On the validated Copilot
entitlement, the SDK reported a 1,178,000-token context window. GPT-5.6 Sol also
remains available and supports `max`, but its reported context window is
1,050,000 tokens. Override `MSGUIDE_COPILOT_MODEL`,
`MSGUIDE_COPILOT_REASONING_EFFORT`, and `MSGUIDE_COPILOT_CONTEXT_TIER` only with
values supported by the selected model.

An integrator must:

1. Build a `CopilotProviderConfig` with an explicit model and an absolute,
   application-owned SDK base directory.
2. Supply a context resolver that reads only deterministic scenario state and
   returns `ApprovedGuidanceContext`: the current observation ID, server-owned
   step ID, approved UIA element indexes/target IDs, and pre-approved citation
   IDs. Do not derive scenario state or completion from model output.
3. Create one provider at application startup and `await provider.start()`.
4. Inject that provider through the existing callable seam.
5. `await provider.close()` during application shutdown.
6. Catch `CopilotProviderFailure` and invoke the caller-owned deterministic
   fallback explicitly. The provider never falls back silently.

Each call creates one bounded, isolated SDK session while reusing the persistent
`CopilotClient` runtime process. Empty mode, an explicit tool allowlist,
disabled session store/memory/infinite sessions, and a deny-by-default
permission handler prevent shell, filesystem, edit, and built-in tool access.
SDK inference is bounded to 45 seconds, the API route to 50 seconds, and the
desktop HTTP request to 55 seconds. The API still rejects a response when its
observation has become more than 60 seconds old; there are no automatic retries.
Only the locally handled terminal `submit_guidance` tool can produce guidance.
It accepts exactly `observationId`, `stepId`, allowlisted `targetId`,
`instruction`, and allowlisted `citationIds`; output cannot set coordinates,
URLs, scenario state, or completion. Partial model prose is never returned.

Screenshot bytes are attached only when the already-approved
`Observation.imageBase64` field is present. They are revalidated and sent as an
in-memory PNG blob; this provider never creates a screenshot file.

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

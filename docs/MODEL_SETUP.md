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

HTTP redirects, automatic retries, and environment proxies are disabled. The call is bounded to ten seconds overall; socket timeouts are eight seconds, with three seconds for connecting. Failures return generic errors without exposing provider response bodies or credentials. The client does not implement provider-specific authentication flows or tool execution.

## What is shared

After **Capture / review → consent → Send**, the remote provider receives the prompt and reviewed observation metadata, including application/window identifiers, timestamp, UIA names, element boxes, and dimensions. Text-only approval still transmits that metadata remotely in model mode.

Screenshot pixels are sent **only if separately opted in for that snapshot**. [src/images.py](../src/images.py) validates size/type/dimensions with Pillow and re-encodes pixel-only PNGs, dropping metadata. It does not redact pixels or perform OCR. Inspect both image and text; discard any sensitive content. Already sent content cannot be recalled, and provider retention is outside this application's control.

To return to local-only guidance, stop the desktop/launcher, set `MSGUIDE_GUIDANCE_PROVIDER=demo` (or remove it), remove the remote-approval and model credential variables from the current process, and restart. Verify the **DEMO** label. See [validation](VALIDATION.md) for the distinction between mocked provider tests and unverified live behavior.

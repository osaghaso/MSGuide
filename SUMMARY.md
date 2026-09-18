# Current status

September 18, 2026. **Desktop-first local prototype. Synthetic checks are not
live-app or live-model acceptance.** Use [README.md](README.md) for the current
behavior and setup; dated evidence remains in the scenario runbooks.

| Area | Evidence / limit |
| --- | --- |
| Windows WPF shell | Cursor companion and compact prompt, with Guide/Fix modes. Guide mode never executes actions. |
| Capture and control | Selected-window Windows Graphics Capture and bounded UI Automation. Semantic actions require fresh target validation; unsupported or uncertain outcomes are not automatically retried. |
| Task planning and progress | Approved observations produce ordered plan segments. Fix mode runs continuously without eight-action or two-minute checkpoints; every action is freshly grounded and observed locally. Plan-limit/observation boundaries refresh automatically after progress on the same approved resource. Genuine resource/input/permission changes and uncertain outcomes still stop. Generic model-suggested completion requires review. |
| Default guidance | Deterministic built-in MSGuide Demo workflow only; unit-tested, no remote calls. |
| Optional models | Explicit Copilot SDK and OpenAI-compatible providers. Consent, bounded deadlines and cancellation remain required; fake-provider regressions do not prove live grounding or latency. |
| Voice | Local Whisper `small.en`, selected microphone, bounded in-memory recording and reviewed transcripts. Windows speech is used for optional playback, not production dictation. |
| Backend and mock actions | Loopback bearer boundary and public sample retrieval. `/v1/actions/*` and `/v1/jobs/*` are simulations; the desktop is the real semantic-action executor. |
| CI and dependencies | Windows CI installs hash-locked Python dependencies and restores locked NuGet packages before backend/desktop synthetic checks. Python locks target CPython 3.11 x64; `global.json` selects the .NET SDK. A checked-in workflow is not evidence of a hosted CI pass. |
| Native acceptance | Camera/voice evidence from September 16 is scoped to its documented environment. Earlier capture/foreground failures and pending acceptance are preserved in the validation history. New behavior needs its own matching live evidence. |
| Pilot / deployment | Not complete, not approved, no production deployment. |

## Start here

- [docs/DEMO_SCENARIOS.md](docs/DEMO_SCENARIOS.md): recommended hackathon stories, two-minute storyboard, scope and recording acceptance gates.
- [README.md](README.md): prerequisites, launcher, workflow, and privacy limits.
- [IMPLEMENTATION.md](IMPLEMENTATION.md): implemented components and actual boundaries.
- [docs/API.md](docs/API.md): route inventory and request constraints.
- [docs/MODEL_SETUP.md](docs/MODEL_SETUP.md): opt-in provider configuration; no endpoint/model defaults or embedded secrets.
- [docs/VALIDATION.md](docs/VALIDATION.md): recorded evidence and interactive rerun instructions.
- [PLAN.md](PLAN.md): checked items tied to evidence; Phase 7 remains incomplete.
- [ROADMAP.md](ROADMAP.md): next acceptance gates, without delivery-date promises.

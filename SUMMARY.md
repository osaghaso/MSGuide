# Current status

September 14, 2026. **Desktop-first local MVP; end-to-end runtime validation blocked.**

| Area | Evidence / limit |
| --- | --- |
| Windows WPF shell | Implemented; .NET 10 build and desktop self-test pass. Not proof of runtime UX. |
| Capture → guidance | Initial 18-check real-window pass; latest runs fail on blank captured pixels. Capture reliability and full foreground/overlay loop remain unverified. |
| Default guidance | Deterministic built-in MSGuide Demo workflow only; unit-tested, no remote calls. |
| Optional model | Explicit OpenAI-compatible transport implemented and unit-tested with mocks. No verified live-model grounding. |
| Voice | Local click-to-toggle dictation, 30-second auto-stop, optional playback; installed Windows speech support required. Manual verification pending. |
| Backend | 10 application routes; 150 pytest tests passed; pip check clean. |
| Desktop integration | Failed at `demo-activate`: activation returned false after rendering/visible layout. Root cause unconfirmed. |
| Security / retrieval / actions | Local bearer boundary, bundled public samples, simulated actions only. No enterprise identity or search. |
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

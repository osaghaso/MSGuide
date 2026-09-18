"""Loopback-only, single-worker demo API. State is bounded, volatile, and not enterprise auth."""

import asyncio
from collections import deque
from contextlib import asynccontextmanager
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import sys
import time
from urllib.parse import urlsplit
from uuid import uuid4

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from pydantic import ValidationError
from starlette.datastructures import Headers

from src import guidance
from src import diagnostics
from src.actions import ActionRunner, ActionStatus
from src.camera_recovery import CameraRecoveryEngine, CameraRecoveryError
from src.copilot_provider import (
    AgencyMicrosoftLearnConfig,
    CopilotProvider,
    CopilotProviderConfig,
    CopilotProviderFailure,
)
from src.model_provider import ModelConfig, OpenAICompatibleProvider
from src.models import (
    ActionRiskLevel, AssistRequest, ExecuteRequest, GuidanceRequest, GuidanceResponse,
    GuidanceResult, LogParameters, PreviewRequest, RequestType, SensitivityLevel,
    WorkItemParameters, OBSERVATION_MAX_AGE_SECONDS, executable_element, guidance_seconds,
)
from src.policy import PolicyEngine
from src.retrieval import RetrieverMock

GUIDANCE_TIMEOUT_SECONDS = 52.0
GUIDANCE_FRESHNESS_HEADROOM_SECONDS = 8.0


def now():
    return datetime.now(timezone.utc)


@dataclass(frozen=True)
class Config:
    token: str = ""
    mode: str = "demo"
    guidance_provider: str = "demo"
    enable_audit: bool = False
    max_body_bytes: int = 3_000_000
    max_records: int = 1000
    model_config: ModelConfig | None = None

    @classmethod
    def from_env(cls):
        return cls(token=os.getenv("MSGUIDE_LOCAL_TOKEN", ""),
                   mode=os.getenv("MSGUIDE_MODE", "demo"),
                   guidance_provider=os.getenv("MSGUIDE_GUIDANCE_PROVIDER", "demo"),
                   enable_audit=os.getenv("MSGUIDE_ENABLE_AUDIT", "").lower() == "true")


class LocalBoundary:
    """Authenticate before body parsing; bound actual streamed bytes, not just Content-Length."""
    def __init__(self, app, config):
        self.app, self.config = app, config

    async def __call__(self, scope, receive, send):
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        headers = Headers(scope=scope)

        async def reject(code, detail):
            await JSONResponse({"detail": detail}, status_code=code)(scope, receive, send)

        peer = scope.get("client")
        if peer:
            try:
                local_peer = ipaddress.ip_address(peer[0]).is_loopback
            except ValueError:
                local_peer = False
            if not local_peer:
                await reject(403, "Loopback client required")
                return
        hosts = headers.getlist("host")
        try:
            host = urlsplit("//" + hosts[0]).hostname if len(hosts) == 1 else None
        except ValueError:
            host = None
        if host not in {"localhost", "127.0.0.1", "::1"}:
            await reject(400, "Local Host required")
            return
        # Native desktop requests have no Origin. Do not authorize browser-origin access.
        if "origin" in headers:
            await reject(403, "Browser origins are not allowed")
            return
        path = scope["path"]
        if path == "/v1" or path.startswith("/v1/") or path.startswith("/admin/"):
            token = self.config.token
            if not token or re.fullmatch(r"[A-Za-z0-9._~+/-]+=*", token) is None:
                await reject(503, "Local authentication is not configured")
                return
            values = headers.getlist("authorization")
            match = re.fullmatch(r"(?i:Bearer) ([A-Za-z0-9._~+/-]+=*)", values[0]) if len(values) == 1 else None
            if match is None or not secrets.compare_digest(match.group(1).encode(), token.encode()):
                await reject(401, "Unauthorized")
                return
            scope.setdefault("state", {})["owner"] = "local"
        lengths = headers.getlist("content-length")
        if lengths and (len(lengths) != 1 or len(lengths[0]) > 20 or not lengths[0].isascii() or not lengths[0].isdigit()):
            await reject(400, "Invalid Content-Length")
            return
        if lengths and int(lengths[0]) > self.config.max_body_bytes:
            await reject(413, "Request body too large")
            return
        body = bytearray()
        while True:
            message = await receive()
            if message["type"] == "http.disconnect":
                return
            body.extend(message.get("body", b""))
            if len(body) > self.config.max_body_bytes:
                await reject(413, "Request body too large")
                return
            if not message.get("more_body", False):
                break
        delivered = False
        scope.setdefault("state", {})["body_bytes"] = len(body)

        async def replay():
            nonlocal delivered
            if not delivered:
                delivered = True
                return {"type": "http.request", "body": bytes(body), "more_body": False}
            return await receive()

        async def private_send(message):
            if message["type"] == "http.response.start":
                message["headers"] = list(message["headers"]) + [(b"cache-control", b"no-store")]
            await send(message)

        await self.app(scope, replay, private_send)


@dataclass(frozen=True)
class Session:
    owner: str
    expires_at: datetime


@dataclass(frozen=True)
class StoredPreview:
    owner: str
    expires_at: datetime
    tool: str
    parameters_json: str


@dataclass
class Grant:
    digest: bytes
    expires_at: datetime
    consumed: bool = False


def _classify_intent(prompt: str) -> RequestType:
    words = set(re.findall(r"\b\w+\b", prompt.casefold()))
    if words & {"how", "step", "steps", "show", "guide", "highlight"}:
        return RequestType.GUIDE
    if words & {"execute", "run", "perform", "create", "delete", "modify"}:
        return RequestType.ACTION
    if words & {"what", "where", "find", "search", "tell", "explain"}:
        return RequestType.RETRIEVE
    return RequestType.EXPLAIN


def fresh(captured_at):
    age = (now() - captured_at).total_seconds()
    if age >= OBSERVATION_MAX_AGE_SECONDS or age < -5:
        raise HTTPException(422, "Observation must be at most 60 seconds old and at most 5 seconds in the future")


def _copilot_context(observation):
    targets = []
    for index, element in enumerate(observation.elements):
        if not observation.automationComplete or not executable_element(element):
            continue
        targets.append({
            "id": f"element-{index}",
            "elementIndex": index,
        })
    citations = []
    if "teams" in observation.application.casefold():
        citations.append({
            "id": "teams-camera-support",
            "citation": {
                "source": "https://support.microsoft.com/en-us/teams/meetings/my-camera-isn-t-working-in-microsoft-teams",
                "title": "My camera isn't working in Microsoft Teams",
            },
        })
    if "settings" in observation.application.casefold():
        citations.append({
            "id": "windows-camera-permissions",
            "citation": {
                "source": "https://support.microsoft.com/en-us/windows/privacy/manage-app-permissions-for-a-camera-in-windows",
                "title": "Manage app permissions for a camera in Windows",
            },
        })
    return {
        "observationId": observation.id,
        "stepId": observation.id,
        "targets": targets,
        "citations": citations,
    }


async def _guidance_connected(awaitable, request: Request, timeout: float):
    async def disconnected():
        while (await request.receive())["type"] != "http.disconnect":
            pass

    work = asyncio.create_task(awaitable)
    disconnect = asyncio.create_task(disconnected())
    try:
        done, _ = await asyncio.wait({work, disconnect}, timeout=timeout,
                                     return_when=asyncio.FIRST_COMPLETED)
        if disconnect in done:
            await disconnect
            raise HTTPException(499, "Guidance request disconnected")
        if work not in done:
            raise TimeoutError
        return await work
    finally:
        # The provider cancels its accepted callback and explicitly aborts owned SDK work.
        # These are owned request tasks, not fire-and-forget work after an HTTP disconnect.
        for pending in (work, disconnect):
            if not pending.done():
                pending.cancel()
        await asyncio.gather(work, disconnect, return_exceptions=True)


def create_app(config: Config | None = None, *, guidance_provider=None) -> FastAPI:
    config = config or Config.from_env()
    diagnostics.configure(os.getenv("MSGUIDE_DIAGNOSTIC_LOG"))
    if config.mode != "demo":
        raise ValueError("Only local MSGUIDE_MODE=demo is supported")
    if config.guidance_provider not in {"demo", "openai-compatible", "copilot-sdk"}:
        raise ValueError("Unknown guidance provider")
    if config.max_body_bytes < 1 or config.max_records < 1:
        raise ValueError("Limits must be positive")
    provider = guidance_provider if guidance_provider is not None else guidance.get_guidance
    if config.guidance_provider == "openai-compatible":
        model_config = config.model_config or ModelConfig.from_env()
        model_config.validate()
        if guidance_provider is None:
            provider = OpenAICompatibleProvider(model_config)
    if config.guidance_provider == "copilot-sdk" and guidance_provider is None:
        base_directory = Path(os.getenv(
            "MSGUIDE_COPILOT_HOME",
            str(Path.home() / ".copilot"),
        )).expanduser().resolve()
        configured_cli = os.getenv("COPILOT_CLI_PATH", "")
        provider = CopilotProvider(
            CopilotProviderConfig(
                model=os.getenv("MSGUIDE_COPILOT_MODEL", "gpt-6-astra"),
                base_directory=base_directory,
                github_token=os.getenv("COPILOT_GITHUB_TOKEN") or None,
                reasoning_effort=os.getenv(
                    "MSGUIDE_COPILOT_REASONING_EFFORT", "low"
                ),
                context_tier=os.getenv(
                    "MSGUIDE_COPILOT_CONTEXT_TIER", "default"
                ),
                cli_path=Path(configured_cli).expanduser().resolve() if configured_cli else None,
                agency_microsoft_learn=AgencyMicrosoftLearnConfig(
                    enabled=os.getenv("MSGUIDE_ENABLE_AGENCY_LEARN", "").lower() == "true",
                ),
            ),
            _copilot_context,
        )
    lifecycle_provider = provider if isinstance(provider, CopilotProvider) else None
    camera_recovery = CameraRecoveryEngine(secrets.token_bytes(32))
    runner = ActionRunner()
    sessions: dict[str, Session] = {}
    previews: dict[str, StoredPreview] = {}
    grants: dict[str, Grant] = {}
    audit = deque(maxlen=1000)
    policy, retriever = PolicyEngine(), RetrieverMock()

    @asynccontextmanager
    async def lifespan(app):
        if lifecycle_provider is not None:
            await lifecycle_provider.start()
        diagnostics.record("backend_started", provider=config.guidance_provider)
        try:
            yield
        finally:
            diagnostics.record("backend_stopping", provider=config.guidance_provider)
            await runner.close()
            if lifecycle_provider is not None:
                await lifecycle_provider.close()
            sessions.clear()
            previews.clear()
            grants.clear()
            audit.clear()
            camera_recovery.clear()

    app = FastAPI(title="MSGuide local demo", version="0.2.0", lifespan=lifespan)
    app.add_middleware(LocalBoundary, config=config)
    app.state.sessions, app.state.previews, app.state.grants = sessions, previews, grants
    app.state.runner, app.state.audit = runner, audit
    app.state.guidance_provider = provider
    app.state.camera_recovery = camera_recovery

    @app.exception_handler(RequestValidationError)
    async def invalid_request(request, exc):
        # Pydantic v2 errors include input: never reflect raw prompt/image/text or tool payloads.
        errors = [{"loc": error["loc"], "type": error["type"],
                   "msg": "Invalid request value"} for error in exc.errors()]
        if os.getenv("MSGUIDE_DEBUG_VALIDATION", "").lower() == "true":
            print(json.dumps(errors, default=str), file=sys.stderr)
        return JSONResponse({"detail": errors}, status_code=422)

    def event(correlation, outcome):
        if config.enable_audit:
            audit.append({"correlationId": correlation, "timestamp": now().isoformat(), "outcome": outcome})

    def owned(store, key, owner):
        item = store.get(key)
        if item is None or item.owner != owner:
            raise HTTPException(404, "Unknown resource")
        if item.expires_at <= now():
            raise HTTPException(410, "Resource expired")
        return item

    def room(store):
        for key, item in list(store.items()):
            if item.expires_at <= now():
                del store[key]
                if store is previews:
                    grants.pop(key, None)
                elif store is sessions:
                    camera_recovery.discard(key)
        if len(store) >= config.max_records:
            raise HTTPException(429, "Local state capacity reached; retry after expiry")

    @app.get("/health")
    async def health():
        return {"status": "ok", "mode": "demo" if config.guidance_provider == "demo" else "model", "version": "0.2.0"}

    @app.post("/v1/sessions")
    async def create_session(request: Request):
        room(sessions)
        session_id = str(uuid4())
        session = Session(request.state.owner, now() + timedelta(hours=1))
        sessions[session_id] = session
        event(session_id, "session_created")
        return {"sessionId": session_id, "expiresAt": session.expires_at.isoformat()}

    @app.post("/v1/guidance", response_model=GuidanceResponse, response_model_exclude_unset=True)
    async def guide(body: GuidanceRequest, request: Request):
        owned(sessions, body.sessionId, request.state.owner)
        if not body.consent:
            raise HTTPException(403, "Explicit capture consent is required")
        fresh(body.observation.capturedAt)
        correlation = str(uuid4())
        diagnostic_token = diagnostics.bind(
            correlation, body.task.taskId if body.task else None, body.task.step if body.task else None
        )
        started = time.monotonic()
        stage = "request_validated"
        diagnostics.record(
            "guidance_started",
            provider=config.guidance_provider,
            bodyBytes=getattr(request.state, "body_bytes", None),
            imageShared=body.observation.imageBase64 is not None,
            elementCount=len(body.observation.elements),
            imageWidth=body.observation.width,
            imageHeight=body.observation.height,
        )
        recovery = None
        try:
            if body.cameraRecovery is not None:
                stage = "camera_recovery"
                verification = body.cameraRecovery.verification
                if verification is not None:
                    fresh(verification.capturedAt)
                    if abs((verification.capturedAt - body.observation.capturedAt).total_seconds()) > 5:
                        raise HTTPException(422, "Camera verification must be captured with the same observation")
                decision = camera_recovery.guide(
                    body.sessionId,
                    body.observation,
                    verification,
                    body.cameraRecovery.profile,
                )
                result, recovery = decision.guidance, decision.recovery
                if result.target is not None and not camera_recovery.target_matches(
                        body.sessionId, body.observation, result.target):
                    raise CameraRecoveryError("Camera target no longer matches the observation")
            else:
                stage = "provider_inference"
                budget = min(GUIDANCE_TIMEOUT_SECONDS, guidance_seconds(
                    body.observation.capturedAt, now(), GUIDANCE_FRESHNESS_HEADROOM_SECONDS
                ))
                if budget <= 0:
                    raise TimeoutError
                arguments = {"task": body.task} if body.task is not None else {}
                result = GuidanceResult.model_validate(
                    await _guidance_connected(
                        provider(body.prompt, body.observation, **arguments), request, budget,
                    )
                )
                if result.mode == "model" and result.status == "completed":
                    result = result.model_copy(update={"status": "completion_candidate"})
                if result.target is not None:
                    stage = "target_validation"
                    matches = []
                    for element in body.observation.elements:
                        if result.target.targetId is not None and element.targetId != result.target.targetId:
                            continue
                        if (result.target.targetId is None
                                and (element.label != result.target.label or element.box != result.target.box)):
                            continue
                        matches.append(
                            element.label == result.target.label
                            and element.box == result.target.box
                            and element.confidence >= result.target.confidence
                            and (result.target.processId is None
                                 or element.processId == result.target.processId)
                            and (result.target.automationId is None
                                 or element.automationId == result.target.automationId)
                            and (result.target.frameworkId is None
                                 or element.frameworkId == result.target.frameworkId)
                            and (result.target.isEnabled is None
                                 or element.isEnabled == result.target.isEnabled)
                            and (result.target.isOffscreen is None
                                 or element.isOffscreen == result.target.isOffscreen)
                            and (result.target.toggleState is None
                                 or element.toggleState == result.target.toggleState)
                            and result.target.action == element.action
                            and result.target.valueHash == element.valueHash
                            and (result.target.action is None
                                 or body.observation.automationComplete and executable_element(element))
                            and (result.target.scrollDirection is None
                                 or result.target.scrollDirection in element.scrollDirections)
                        )
                    if matches != [True]:
                        raise ValueError("Unsupported target")
            stage = "freshness_validation"
            fresh(body.observation.capturedAt)
            diagnostics.record(
                "guidance_completed",
                status=result.status,
                targetSelected=result.target is not None,
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
        except asyncio.CancelledError:
            diagnostics.record("guidance_cancelled", stage=stage,
                               elapsedMs=round((time.monotonic() - started) * 1000))
            raise
        except asyncio.TimeoutError:
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode="timeout",
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            raise HTTPException(
                504,
                "Guidance timed out",
                headers={"X-MSGuide-Correlation-ID": correlation},
            ) from None
        except CopilotProviderFailure as exc:
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode=f"provider-{exc.code}",
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            print(
                f"MSGuide Copilot provider failure: {exc.code}",
                file=sys.stderr,
                flush=True,
            )
            if exc.code == "timeout":
                raise HTTPException(
                    504,
                    "Guidance timed out",
                    headers={"X-MSGuide-Correlation-ID": correlation},
                ) from None
            raise HTTPException(
                502,
                "Guidance provider failed",
                headers={
                    "X-MSGuide-Error-Code": f"provider-{exc.code}",
                    "X-MSGuide-Correlation-ID": correlation,
                },
            ) from None
        except CameraRecoveryError as exc:
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode="camera-recovery",
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            raise HTTPException(422, str(exc)) from None
        except (ValidationError, ValueError):
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode="guidance-invalid-result",
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            raise HTTPException(
                502,
                "Invalid guidance result",
                headers={
                    "X-MSGuide-Error-Code": "guidance-invalid-result",
                    "X-MSGuide-Correlation-ID": correlation,
                },
            ) from None
        except HTTPException as exc:
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode="http-error",
                status=exc.status_code,
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            raise
        except Exception as exc:
            # Provider errors can contain screen text or credentials. Never log or reflect them.
            exception_type = type(exc).__name__
            if not exception_type.isascii() or not exception_type.isidentifier():
                exception_type = "Unknown"
            print(
                f"MSGuide guidance failure: {exception_type}",
                file=sys.stderr,
                flush=True,
            )
            diagnostics.record(
                "guidance_failed",
                stage=stage,
                errorCode="guidance-unexpected",
                errorType=exception_type,
                elapsedMs=round((time.monotonic() - started) * 1000),
            )
            raise HTTPException(
                502,
                "Guidance provider failed",
                headers={
                    "X-MSGuide-Error-Code": "guidance-unexpected",
                    "X-MSGuide-Error-Type": exception_type,
                    "X-MSGuide-Correlation-ID": correlation,
                },
            ) from None
        finally:
            diagnostics.reset(diagnostic_token)
        owned(sessions, body.sessionId, request.state.owner)
        event(correlation, "guidance_" + result.status)
        payload = {
            "instruction": result.instruction,
            "status": result.status,
            "target": (result.target.model_dump(exclude_unset=True)
                       if result.target is not None else None),
            "citations": result.citations,
            "mode": result.mode,
        }
        if recovery is not None:
            payload["cameraRecovery"] = recovery
        if result.remainingWork is not None:
            payload["remainingWork"] = result.remainingWork
        if body.task is not None:
            payload.update(taskId=body.task.taskId, step=body.task.step)
        return GuidanceResponse(**payload, correlationId=correlation,
                                observationId=body.observation.id, windowId=body.observation.windowId)

    @app.post("/v1/assist")
    async def assist(body: AssistRequest, request: Request):
        owned(sessions, body.sessionId, request.state.owner)
        if body.context:
            if body.context.sessionId != body.sessionId:
                raise HTTPException(422, "Context session does not match request")
            if body.context.sensitivity != SensitivityLevel.PUBLIC:
                raise HTTPException(403, "Only synthetic/public demo context is supported")
            fresh(body.context.timestamp)
        kind = _classify_intent(body.prompt)
        passages = await retriever.retrieve(body.prompt) if kind != RequestType.ACTION else []
        answer = "\n\n".join(p.content for p in passages) or "Demo only: no supporting sample passage was found; no action was performed."
        correlation = str(uuid4())
        event(correlation, "assist_" + kind.value)
        return {"correlationId": correlation, "requestType": kind.value, "answer": answer,
                "citations": [{"source": p.sourceUri, "title": p.title, "modified": p.modifiedTime.isoformat()} for p in passages],
                "responseModes": body.responseModes, "mode": "demo"}

    @app.post("/v1/actions/preview")
    async def preview(body: PreviewRequest, request: Request):
        risk = policy.classify_action_risk(body.tool, body.parameters)
        if risk in {ActionRiskLevel.HIGH, ActionRiskLevel.BLOCKED}:
            status = "STEP_UP_REQUIRED" if risk == ActionRiskLevel.HIGH else "DENIED"
            event(str(uuid4()), status)
            return JSONResponse({"status": status, "detail": "External actions are disabled"}, status_code=403)
        try:
            model = WorkItemParameters if body.tool == "create_work_item" else LogParameters
            parameters = model.model_validate(body.parameters).model_dump()
        except ValidationError:
            raise HTTPException(422, "Invalid mock tool parameters") from None
        room(previews)
        preview_id = str(uuid4())
        item = StoredPreview(request.state.owner, now() + timedelta(minutes=5), body.tool,
                             json.dumps(parameters, sort_keys=True, separators=(",", ":")))
        previews[preview_id] = item
        event(preview_id, "preview_created")
        return {"previewId": preview_id, "tool": item.tool, "parameters": parameters,
                "effects": "Simulation only; no external effects.", "riskLevel": risk.value,
                "requiresConfirmation": True, "expiryTime": item.expires_at.isoformat(), "mock": True}

    @app.post("/v1/actions/{previewId}/confirm")
    async def confirm(previewId: str, request: Request):
        item = owned(previews, previewId, request.state.owner)
        if previewId in grants:
            raise HTTPException(409, "Preview already confirmed or consumed")
        token = secrets.token_urlsafe(32)
        expires = min(item.expires_at, now() + timedelta(minutes=5))
        grants[previewId] = Grant(hashlib.sha256(token.encode()).digest(), expires)
        event(previewId, "confirmed")
        return {"grantToken": token, "expiresIn": max(0, int((expires - now()).total_seconds())),
                "expiresAt": expires.isoformat(), "mock": True}

    @app.post("/v1/actions/{previewId}/execute")
    async def execute(previewId: str, body: ExecuteRequest, request: Request):
        item = owned(previews, previewId, request.state.owner)
        grant = grants.get(previewId)
        if grant is None or not secrets.compare_digest(grant.digest, hashlib.sha256(body.grantToken.encode()).digest()):
            raise HTTPException(403, "Invalid execution grant")
        if grant.consumed:
            raise HTTPException(409, "Grant already consumed")
        if grant.expires_at <= now():
            raise HTTPException(410, "Grant expired")
        if policy.classify_action_risk(item.tool, {}) not in {ActionRiskLevel.LOW, ActionRiskLevel.MEDIUM}:
            raise HTTPException(403, "Tool no longer permitted")
        room(runner.jobs)
        if sum(j.status in {ActionStatus.PENDING, ActionStatus.RUNNING} for j in runner.jobs.values()) >= 16:
            raise HTTPException(429, "Too many active mock jobs")
        # No awaits between validation, consumption, and scheduling: atomic on this single event loop.
        grant.consumed = True
        job = runner.start(str(uuid4()), request.state.owner, item.tool, json.loads(item.parameters_json))
        event(previewId, "mock_execution_started")
        return job.response()

    @app.get("/v1/jobs/{jobId}")
    async def job_status(jobId: str, request: Request):
        return owned(runner.jobs, jobId, request.state.owner).response()

    @app.post("/v1/jobs/{jobId}/cancel")
    async def cancel_job(jobId: str, request: Request):
        job = owned(runner.jobs, jobId, request.state.owner)
        runner.cancel(job)
        event(jobId, "cancel_" + job.status.value)
        return job.response()

    @app.get("/admin/audit-log")
    async def audit_log(limit: int = Query(100, ge=1, le=1000)):
        if not config.enable_audit:
            raise HTTPException(404, "Audit is disabled")
        return {"count": len(audit), "events": list(audit)[-limit:]}

    return app


app = create_app()


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=8000, access_log=False)

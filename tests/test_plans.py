"""Plan contracts and providers use synthetic observations and in-process transports only."""

import asyncio
from copy import deepcopy
import json
from pathlib import Path
import re
from types import SimpleNamespace

import httpx
import pytest
from pydantic import ValidationError

from src.copilot_provider import (
    AgencyMicrosoftLearnConfig, COPILOT_STARTUP_TIMEOUT_SECONDS,
    CopilotProvider, CopilotProviderConfig, CopilotProviderFailure,
)
from src.main import Config, create_app
from src.model_provider import OpenAICompatibleProvider
from src.models import EMPTY_VALUE_HASH, MAX_PLAN_STEPS, Observation, PlanInput, plan_for
from tests.local_client import TestClient
from tests.test_copilot_provider import FakeClient, approved_context, observation
from tests.test_model_provider import approved, reply


@pytest.fixture(autouse=True)
def isolated_environment(monkeypatch):
    monkeypatch.delenv("COPILOT_CLI_PATH", raising=False)
    monkeypatch.delenv("MSGUIDE_DIAGNOSTIC_LOG", raising=False)


def observed(image=False):
    value = observation(image=image).model_dump(mode="json")
    value["resourceId"] = "resource-synthetic"
    value["elements"][0]["controlId"] = "control-continue"
    return Observation.model_validate(value)


def segment(count=3, boundary="completion_candidate"):
    return {
        "steps": [
            {"kind": "action", "instruction": f"Perform synthetic step {index + 1}.", "targetIndex": 0}
            if index == 0 else
            {"kind": "action", "instruction": f"Perform synthetic step {index + 1}.",
             "intent": {"role": "button", "label": "Continue", "action": "invoke"}}
            for index in range(count)
        ],
        "boundary": {"kind": boundary, "reason": "Synthetic segment boundary.",
                     "needed": "" if boundary == "completion_candidate" else "Review the next resource or provide information."},
    }


def make_provider(kind, output, tmp_path):
    if kind == "copilot":
        value = {**output, "observationId": "obs-1", "stepId": "step-2"}
        client = FakeClient(value)
        return CopilotProvider(
            CopilotProviderConfig(model="synthetic-model", base_directory=tmp_path),
            approved_context, client_factory=lambda **_: client,
        ), client.sessions
    sent = []

    def handler(request):
        sent.append(json.loads(request.content))
        return httpx.Response(200, json=reply(output))

    return OpenAICompatibleProvider(approved(), transport=httpx.MockTransport(handler)), sent


async def run_provider(model, evidence, *, plan=True):
    if isinstance(model, CopilotProvider):
        await model.start()
    try:
        return await model("Complete the synthetic task.", evidence, plan=plan)
    finally:
        if isinstance(model, CopilotProvider):
            await model.close()


@pytest.mark.parametrize("kind", ["copilot", "openai"])
@pytest.mark.parametrize("count,boundary", [(3, "completion_candidate"), (17, "resource"), (32, "plan_limit")])
@pytest.mark.asyncio
async def test_one_provider_call_returns_entire_bounded_segment(kind, count, boundary, tmp_path):
    evidence = observed(image=True)
    model, sent = make_provider(kind, {"instruction": "Follow the synthetic plan.", "status": "next_step",
                                     "plan": segment(count, boundary)}, tmp_path)
    result = await run_provider(model, evidence)
    assert len(sent) == 1 and len(result.plan.steps) == count
    assert result.target is None and result.status == "next_step"
    assert result.plan.windowId == evidence.windowId and result.plan.resourceId == evidence.resourceId
    assert result.plan.steps[0].controlId == "control-continue"
    assert all(step.controlId is None for step in result.plan.steps[1:])
    assert result.plan.boundary.kind == boundary
    if kind == "copilot":
        payload = json.loads(sent[0].sent[0])
        assert payload["planRequested"] is True
        assert payload["serverApproved"]["targets"] == [{"targetId": "continue", "elementIndex": 0}]
        assert payload["untrustedObservation"]["elements"][0]["controlId"] == "control-continue"
        assert len(sent[0].sent[1]) == 1
    else:
        payload = json.loads(sent[0]["messages"][1]["content"][1]["text"])
        assert payload["planRequested"] is True and "planSchema" in payload
        assert sent[0]["max_tokens"] == 8000


def malformed_plans():
    values = []
    for field, value in (
        ("kind", "shell"), ("value", "unexpected"), ("scrollDirection", "down"),
        ("instruction", "x" * 501), ("controlId", "invented"), ("targetIndex", 199),
        ("targetId", "invented"), ("parameters", {"command": "not permitted"}),
    ):
        plan = segment()
        plan["steps"][2][field] = value
        values.append(plan)
    for intent in (
        {"role": "button", "label": "Continue", "action": "click"},
        {"role": "button", "label": "Continue", "action": "toggle"},
        {"role": "button", "label": "Continue", "action": "select", "isSelected": True},
        {"role": "button", "label": " ", "action": "invoke"},
    ):
        plan = segment()
        plan["steps"][2]["intent"] = intent
        values.append(plan)
    plan = segment()
    plan["steps"][2] = {"kind": "action", "instruction": "Unapproved ID.", "targetId": "unseen"}
    values.extend([plan, segment(MAX_PLAN_STEPS + 1, "plan_limit"), segment(MAX_PLAN_STEPS)])
    for boundary in (
        {"kind": "unknown", "reason": "No supported boundary.", "needed": "input"},
        {"kind": "permission", "reason": "Permission is required.", "needed": ""},
    ):
        plan = segment()
        plan["boundary"] = boundary
        values.append(plan)
    plan = segment()
    plan["steps"][1] = {"kind": "manual", "instruction": "Do not execute beyond a handoff."}
    values.append(plan)
    return values


@pytest.mark.parametrize("kind", ["copilot", "openai"])
@pytest.mark.parametrize("plan", malformed_plans())
@pytest.mark.asyncio
async def test_malformed_later_step_rejects_whole_model_result(kind, plan, tmp_path):
    model, sent = make_provider(kind, {"instruction": "Validate every step.", "status": "next_step", "plan": plan}, tmp_path)
    with pytest.raises((ValueError, CopilotProviderFailure)):
        await run_provider(model, observed())
    assert len(sent) == 1


@pytest.mark.parametrize("kind", ["copilot", "openai"])
@pytest.mark.asyncio
async def test_plan_request_cannot_silently_fall_back_to_single_step(kind, tmp_path):
    output = {"instruction": "Legacy step.", "status": "next_step",
              "targetId" if kind == "copilot" else "targetIndex": "continue" if kind == "copilot" else 0}
    model, _ = make_provider(kind, output, tmp_path)
    with pytest.raises((ValueError, CopilotProviderFailure)):
        await run_provider(model, observed())


@pytest.mark.parametrize("kind", ["copilot", "openai"])
@pytest.mark.parametrize("boundary", ["resource", "needs_input", "permission", "observation", "unsupported"])
@pytest.mark.asyncio
async def test_boundaries_return_needed_resource_without_actions(kind, boundary, tmp_path):
    model, sent = make_provider(kind, {"instruction": "Wait for explicit review.", "status": "next_step",
                                     "plan": segment(0, boundary)}, tmp_path)
    result = await run_provider(model, observed())
    assert len(sent) == 1 and result.plan.steps == [] and result.plan.boundary.needed
    assert result.plan.boundary.kind == boundary and result.target is None


@pytest.mark.parametrize("kind", ["copilot", "openai"])
def test_api_preserves_plan_segment_and_local_mock_actions_remain_unused(kind, tmp_path):
    model, sent = make_provider(kind, {"instruction": "A whole plan.", "status": "next_step", "plan": segment()}, tmp_path)
    config = Config(token="synthetic-plan-token", guidance_provider="copilot-sdk" if kind == "copilot" else "openai-compatible",
                    model_config=None if kind == "copilot" else approved())
    with TestClient(create_app(config, guidance_provider=model),
                    headers={"Authorization": "Bearer synthetic-plan-token"}) as client:
        sid = client.post("/v1/sessions").json()["sessionId"]
        response = client.post("/v1/guidance", json={
            "sessionId": sid, "consent": True, "prompt": "Synthetic task", "planSegments": True,
            "observation": observed(image=True).model_dump(mode="json"),
        })
        assert response.status_code == 200, response.text
        result = response.json()
        assert len(sent) == 1 and len(result["plan"]["steps"]) == 3 and result["target"] is None
        assert result["plan"]["resourceId"] == "resource-synthetic"
        assert not client.app.state.runner.jobs


@pytest.mark.parametrize("drift", ["window", "resource", "control", "value", "incomplete"])
def test_api_rejects_forged_canonical_plan_before_returning_any_action(drift):
    evidence = observed()
    plan = plan_for(PlanInput.model_validate(segment()), evidence).model_dump(mode="json")
    if drift == "window":
        plan["windowId"] = "other-window"
    elif drift == "resource":
        plan["resourceId"] = "other-resource"
    elif drift == "control":
        plan["steps"][2]["controlId"] = "invented-control"
    elif drift == "value":
        plan["steps"][2]["value"] = "unapproved"
    else:
        evidence = evidence.model_copy(update={"automationComplete": False})

    async def provider(*_, **__):
        return {"instruction": "Synthetic invalid result.", "status": "next_step", "plan": plan}

    with TestClient(create_app(Config(token="synthetic-plan-token"), guidance_provider=provider),
                    headers={"Authorization": "Bearer synthetic-plan-token"}) as client:
        sid = client.post("/v1/sessions").json()["sessionId"]
        response = client.post("/v1/guidance", json={
            "sessionId": sid, "consent": True, "prompt": "Synthetic task", "planSegments": True,
            "observation": evidence.model_dump(mode="json"),
        })
        assert response.status_code == 502 and "plan" not in response.json()
        assert not client.app.state.runner.jobs


def test_deferred_writes_require_empty_field_and_semantic_inputs_are_bounded():
    plan = segment(0)
    plan["steps"] = [{"kind": "action", "instruction": "Replace an empty field.",
                      "intent": {"role": "edit", "label": "Synthetic field", "action": "set_value"}, "value": "new"}]
    built = plan_for(PlanInput.model_validate(plan), observed())
    assert built.steps[0].controlId is None and built.steps[0].valueHash == EMPTY_VALUE_HASH
    for value in ("x" * 1001, "invalid\0input", 12, True):
        changed = deepcopy(plan)
        changed["steps"][0]["value"] = value
        with pytest.raises((ValidationError, ValueError)):
            plan_for(PlanInput.model_validate(changed), observed())


def test_demo_plan_and_partial_descriptions_do_not_invent_observed_authority():
    from src.guidance import get_guidance

    evidence = observed().model_copy(update={"application": "MSGuide Demo", "automationComplete": False})
    result = asyncio.run(get_guidance("Finish the demo", evidence, plan=True))
    assert len(result.plan.steps) == 3 and all(step.controlId is None for step in result.plan.steps)
    assert [step.intent.label for step in result.plan.steps] == ["View logs", "Open troubleshooting", "Mark resolved"]
    assert result.plan.boundary.kind == "completion_candidate"


def test_launcher_readiness_exceeds_provider_startup_with_headroom():
    script = (Path(__file__).resolve().parents[1] / "scripts" / "Start-MSGuide.ps1").read_text(encoding="utf-8-sig")
    seconds = int(re.search(r"\$readinessSeconds\s*=\s*(\d+)", script).group(1))
    assert seconds >= COPILOT_STARTUP_TIMEOUT_SECONDS + 15
    assert "stage=$readinessStage" in script and "lastError=$readinessError" in script


@pytest.mark.asyncio
async def test_slow_valid_startup_and_sanitized_startup_diagnostics(tmp_path, monkeypatch):
    from src import diagnostics

    events = []
    monkeypatch.setattr(diagnostics, "record", lambda event, **fields: events.append((event, fields)))
    client = FakeClient(None)

    async def slow_start():
        await asyncio.sleep(0.06)

    client.start = slow_start
    model = CopilotProvider(
        CopilotProviderConfig(model="synthetic", base_directory=tmp_path, startup_timeout_seconds=0.2),
        approved_context, client_factory=lambda **_: client,
    )
    await model.start()
    await model.close()
    assert [event for event, _ in events if event.startswith("copilot_start")] == ["copilot_starting", "copilot_started"]
    assert events[0][1]["deadlineSeconds"] == 0.2
    assert next(fields for event, fields in events if event == "copilot_started")["elapsedMs"] >= 50
    assert next(fields for event, fields in events if event == "copilot_auth_checked") == {"authenticated": True}


@pytest.mark.asyncio
async def test_plan_permission_handler_does_not_fetch_resources_even_when_legacy_retrieval_is_enabled(tmp_path):
    from copilot.rpc import PermissionDecisionReject

    output = {"instruction": "Request the next resource.", "status": "next_step",
              "observationId": "obs-1", "stepId": "step-2", "plan": segment(0, "resource")}
    client = FakeClient(output)
    model = CopilotProvider(
        CopilotProviderConfig(model="synthetic", base_directory=tmp_path,
                              agency_microsoft_learn=AgencyMicrosoftLearnConfig(enabled=True)),
        approved_context, client_factory=lambda **_: client,
    )
    result = await run_provider(model, observed())
    options = client.sessions[0].options
    assert "mcp_servers" not in options and list(options["available_tools"]) == ["custom:submit_guidance"]
    permission = options["on_permission_request"](
        SimpleNamespace(server_name="msft-learn", tool_name="microsoft_docs_search", read_only=True), None,
    )
    assert isinstance(permission, PermissionDecisionReject)
    assert result.plan.boundary.kind == "resource" and result.plan.steps == []

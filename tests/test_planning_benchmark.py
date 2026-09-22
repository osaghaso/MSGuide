"""Repeatable offline check for the synthetic benchmark; no live inference."""

from copy import deepcopy
from pathlib import Path

import pytest

from scripts.benchmark_planning import (
    EXPECTED, PROMPT, approved_context, benchmark, fixture, score, summarize,
)
from src.copilot_provider import CopilotProvider, CopilotProviderConfig
from tests.test_copilot_provider import FakeClient


@pytest.mark.asyncio
async def test_benchmark_fixture_and_scoring(monkeypatch):
    monkeypatch.delenv("COPILOT_CLI_PATH", raising=False)
    plain, pictured = fixture(), fixture(True)
    assert plain.id != pictured.id and plain.capturedAt < pictured.capturedAt
    assert plain.imageBase64 is None and pictured.imageBase64
    # Drop only intentionally fresh identifiers and image bytes before comparing both modes.
    def content(observation):
        data = observation.model_dump(mode="json", exclude={
            "id", "windowId", "resourceId", "capturedAt", "imageBase64",
        })
        for element in data["elements"]:
            element.pop("targetId")
            element.pop("controlId")
        return data

    assert content(plain) == content(pictured)
    assert approved_context(plain)["stepId"] != approved_context(plain)["stepId"]

    def output(body):
        return {
            "observationId": body["serverApproved"]["observationId"],
            "stepId": body["serverApproved"]["stepId"],
            "status": "next_step", "instruction": "Prepare the practice report, then stop.",
            "plan": {
                "steps": [{"kind": "action", "instruction": f"Use {label}.", "targetIndex": index + 2,
                           **({"value": value} if value is not None else {})}
                          for index, (_, label, _, value, _) in enumerate(EXPECTED)],
                "boundary": {"kind": "resource", "reason": "A separate preview window opens.",
                             "needed": "Review the preview in the separate window."},
            },
        }

    client = FakeClient(output)
    model = CopilotProvider(
        CopilotProviderConfig(model="synthetic-model", base_directory=Path.cwd(),
                              reasoning_effort="low", context_tier="default"),
        approved_context, client_factory=lambda **_: client,
    )
    try:
        await model.start()
        good = await model(PROMPT, plain, plan=True)
    finally:
        await model.close()
    assert score(good, plain)["valid"] and score(good, plain)["correct"]
    assert len(good.plan.steps) == 3 and client.sessions[0].disconnected
    assert list(client.sessions[0].options["available_tools"]) == ["custom:submit_guidance"]

    for change in ("order", "value", "boundary", "intent", "scope"):
        bad = deepcopy(good)
        if change == "order":
            bad.plan.steps.reverse()
        elif change == "value":
            bad.plan.steps[0].value = "Wrong title"
        elif change == "boundary":
            bad.plan.boundary.kind = "completion_candidate"
        elif change == "intent":
            bad.plan.steps[0].controlId = None
            bad.plan.steps[0].intent.automationId = "invented"
        else:
            bad.plan.windowId = "wrong-window"
        assert not score(bad, plain)["correct"]

    rows = [{"model": "synthetic-model", "image_mode": "uia_only", "attempted": True,
             "valid": valid, "correct": correct, "planning_ms": ms, "startup_ms": 1,
             "cleanup_ms": 2, "total_ms": ms + 3}
            for valid, correct, ms in [(True, True, 10), (True, False, 20), (False, False, 30)]]
    summary = summarize(rows)[0]
    assert (summary["samples"], summary["valid"], summary["correct"], summary["failures"]) == (3, 2, 1, 2)
    assert summary["planning_all_attempts"]["median_ms"] == 20
    assert summary["planning_correct"]["median_ms"] == 10
    with pytest.raises(ValueError):
        await benchmark(["synthetic-model", "another-model"], 4)

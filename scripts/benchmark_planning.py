"""Synthetic planning only; never captures a screen or executes a returned action.

Run from the repository: python -m scripts.benchmark_planning --output <report.json>
Each sample owns a fresh provider/runtime. Planning includes session setup and
image validation, but excludes runtime startup, fixture generation, and cleanup.
"""

import argparse
import asyncio
import base64
from datetime import datetime, timezone
from importlib.metadata import version
from io import BytesIO
import json
import os
from pathlib import Path
import statistics
import time
from uuid import uuid4

from copilot import CopilotClient
from PIL import Image, ImageDraw, ImageFont

from src.copilot_provider import CopilotProvider, CopilotProviderConfig, CopilotProviderFailure
from src.models import EMPTY_VALUE_HASH, Observation, executable_element, validate_plan


MAX_CALLS = 12
IMAGE_MODES = ("uia_only", "uia_and_synthetic_screenshot")
PROMPT = (
    "Prepare a practice report in this sample app: set Report title to "
    "'Sample report', turn off Include draft notes, then select Open preview, "
    "in that order. The preview opens in another window. Stop at that resource "
    "boundary; do not interact with the other window or claim the report is complete."
)
EXPECTED = [
    ("edit", "Report title", "set_value", "Sample report", None),
    ("checkbox", "Include draft notes", "toggle", None, "on"),
    ("button", "Open preview", "invoke", None, None),
]


def fixture(image=False):
    """Identical public UIA and visual content; only the image attachment varies."""
    width, height = 960, 600
    nonce = uuid4().hex
    elements = [
        {"role": "text", "label": "Practice report - not yet prepared", "box": [0.05, 0.05, 0.9, 0.08]},
        {"role": "text", "label": "Open preview opens a separate preview window.",
         "box": [0.05, 0.15, 0.9, 0.08]},
        {"role": "edit", "label": "Report title", "box": [0.05, 0.3, 0.65, 0.12],
         "action": "set_value", "isReadOnly": False, "valueHash": EMPTY_VALUE_HASH, "valueLength": 0},
        {"role": "checkbox", "label": "Include draft notes", "box": [0.05, 0.5, 0.65, 0.1],
         "action": "toggle", "toggleState": "on"},
        {"role": "button", "label": "Open preview", "box": [0.05, 0.7, 0.3, 0.1], "action": "invoke",
         "helpText": "Opens a separate preview window; the selected window cannot show the result."},
    ]
    for index, element in enumerate(elements):
        element.update(confidence=1.0, isEnabled=True, isOffscreen=False, isPassword=False,
                       targetable="action" in element, frameworkId="WPF")
        if "action" in element:
            element.update(targetId=f"target-{nonce}-{index}", controlId=f"control-{nonce}-{index}",
                           automationId=f"sample-control-{index}")
    png = None
    if image:
        canvas = Image.new("RGB", (width, height), "white")
        draw = ImageDraw.Draw(canvas)
        font = ImageFont.load_default(size=22)
        for element in elements:
            x, y, w, h = element["box"]
            x, y, w, h = x * width, y * height, w * width, h * height
            if element["role"] == "edit":
                draw.text((x, y - 28), element["label"], fill="black", font=font)
                draw.rectangle((x, y, x + w, y + h), outline="black", width=2)
            elif element["role"] == "checkbox":
                draw.rectangle((x, y, x + 24, y + 24), fill="#145da0")
                draw.text((x + 5, y - 1), "x", fill="white", font=font)
                draw.text((x + 36, y), element["label"], fill="black", font=font)
            else:
                if element["role"] == "button":
                    draw.rectangle((x, y, x + w, y + h), fill="#eeeeee", outline="black")
                draw.text((x + 8, y + 8), element["label"], fill="black", font=font)
        stream = BytesIO()
        canvas.save(stream, format="PNG")
        png = base64.b64encode(stream.getvalue()).decode("ascii")
    return Observation(
        id=f"obs-{nonce}", windowId=f"window-{nonce}", resourceId=f"resource-{nonce}",
        application="Public synthetic report form", capturedAt=datetime.now(timezone.utc),
        width=width, height=height, ocrText="", elements=elements, imageBase64=png,
    )


def approved_context(observation):
    return {
        "observationId": observation.id,
        "stepId": f"step-{uuid4().hex}",
        "targets": [{"id": element.targetId, "elementIndex": index}
                    for index, element in enumerate(observation.elements)
                    if observation.automationComplete and executable_element(element)],
    }


def make_provider(model):
    clients = []

    def client_factory(**options):
        client = CopilotClient(**options, log_level="error")
        clients.append(client)
        return client

    provider = CopilotProvider(
        CopilotProviderConfig(
            model=model,
            base_directory=Path(os.getenv("MSGUIDE_COPILOT_HOME", str(Path.home() / ".copilot"))).resolve(),
            github_token=os.getenv("COPILOT_GITHUB_TOKEN") or None,
            reasoning_effort="low", context_tier="default",
        ),
        approved_context, client_factory=client_factory,
    )
    return provider, clients[0]


def score(result, observation):
    if result.plan is None:
        return {"valid": False, "correct": False, "issues": ["missing_plan"]}
    try:
        validate_plan(result.plan, observation)
    except ValueError:
        return {"valid": False, "correct": False, "issues": ["invalid_plan_grounding"]}
    actual = [
        (step.intent.role, step.intent.label, step.intent.action, step.value, step.intent.toggleState)
        if step.kind == "action" else ("manual", None, None, None, None)
        for step in result.plan.steps
    ]
    issues = []
    if result.status != "next_step" or result.target is not None:
        issues.append("unexpected_status_or_root_target")
    if actual != EXPECTED:
        issues.append("wrong_actions_order_value_or_precondition")
    if result.plan.boundary.kind != "resource" or not result.plan.boundary.needed.strip():
        issues.append("wrong_boundary")
    for step in result.plan.steps:
        if step.intent is not None:
            matches = [element for element in observation.elements
                       if (element.role, element.label, element.action)
                       == (step.intent.role, step.intent.label, step.intent.action)]
            if len(matches) != 1 or any(
                getattr(step.intent, field) not in (None, getattr(matches[0], field))
                for field in ("automationId", "frameworkId")
            ):
                issues.append("unresolvable_intent")
                break
    return {
        "valid": True, "correct": not issues, "issues": issues,
        "step_count": len(actual), "boundary": result.plan.boundary.kind,
        "observed_control_count": sum(step.controlId is not None for step in result.plan.steps),
    }


def elapsed_stats(values):
    return {
        "count": len(values),
        "median_ms": round(statistics.median(values), 3),
        "mean_ms": round(statistics.mean(values), 3),
        "min_ms": round(min(values), 3),
        "max_ms": round(max(values), 3),
        "stdev_ms": round(statistics.stdev(values), 3) if len(values) > 1 else 0.0,
    } if values else {"count": 0}


def summarize(samples):
    groups = []
    for model, mode in dict.fromkeys((s["model"], s["image_mode"]) for s in samples):
        rows = [s for s in samples if s["model"] == model and s["image_mode"] == mode]
        groups.append({
            "model": model, "image_mode": mode, "samples": len(rows),
            "attempted": sum(s["attempted"] for s in rows),
            "valid": sum(s["valid"] for s in rows), "correct": sum(s["correct"] for s in rows),
            "failures": sum(not s["correct"] or bool(s.get("error")) for s in rows),
            "planning_all_attempts": elapsed_stats([s["planning_ms"] for s in rows if s["attempted"]]),
            "planning_correct": elapsed_stats([s["planning_ms"] for s in rows if s["correct"]]),
            "startup": elapsed_stats([s["startup_ms"] for s in rows]),
            "cleanup": elapsed_stats([s["cleanup_ms"] for s in rows]),
            "total": elapsed_stats([s["total_ms"] for s in rows]),
        })
    return groups


async def sample(model, image_mode, repetition):
    provider, _ = make_provider(model)
    row = {"model": model, "image_mode": image_mode, "repetition": repetition,
           "attempted": False, "valid": False, "correct": False}
    started = time.perf_counter()
    try:
        await provider.start()
        row["startup_ms"] = (time.perf_counter() - started) * 1000
        observation = fixture(image_mode != "uia_only")
        row["captured_at"] = observation.capturedAt.isoformat()
        row["observation_id"] = observation.id
        row["image_bytes"] = len(base64.b64decode(observation.imageBase64 or ""))
        inference = time.perf_counter()
        row["attempted"] = True
        try:
            result = await provider(PROMPT, observation, plan=True)
        finally:
            row["planning_ms"] = (time.perf_counter() - inference) * 1000
        row.update(score(result, observation))
    except CopilotProviderFailure as error:
        row["error"] = error.code
    finally:
        row.setdefault("startup_ms", (time.perf_counter() - started) * 1000)
        cleanup = time.perf_counter()
        try:
            await provider.close()
        except CopilotProviderFailure:
            row["error"] = "cleanup_failed"
        row["cleanup_ms"] = (time.perf_counter() - cleanup) * 1000
        row["total_ms"] = (time.perf_counter() - started) * 1000
    return row


async def benchmark(models, repeats):
    if not models or len(models) != len(set(models)) or repeats < 1 or len(models) * 2 * repeats > MAX_CALLS:
        raise ValueError("Use unique models and positive repeats totaling at most 12 requests")
    provider, client = make_provider(models[0])
    try:
        await provider.start()
        async with asyncio.timeout(30):
            available = await client.list_models()
    finally:
        await provider.close()
    catalog = {model.id: model for model in available}
    for name in models:
        model = catalog.get(name)
        if (model is None or model.policy is not None and model.policy.state == "disabled"
                or not model.capabilities.supports.vision
                or "low" not in (model.supported_reasoning_efforts or [])):
            raise ValueError("A requested model is unavailable or lacks vision/low reasoning support")
    report = {
        "sdk_version": version("github-copilot-sdk"),
        "started_at": datetime.now(timezone.utc).isoformat(),
        "reasoning_effort": "low", "context_tier": "default", "repeats": repeats,
        "available_models": [{"id": model.id, "vision": model.capabilities.supports.vision,
                              "reasoning_efforts": model.supported_reasoning_efforts,
                              "policy": model.policy.state if model.policy else None}
                             for model in available],
        "fixture": "public-report-form-v1", "expected_steps": EXPECTED, "expected_boundary": "resource",
        "method": "Sequential, fresh runtime/session per sample; reverse cell order on alternate repetitions. "
                  "Planning includes session setup and attachment validation, excludes startup and cleanup. "
                  "No warmup, retries by this harness, execution, screen capture, or external tools.",
        "limitations": "One small synthetic UI, three repetitions per cell by default; not a production "
                       "quality estimate. Resource handoff tested, final-page completion not tested. "
                       "SDK/provider may internally retry rejected tool submissions. No token/cost telemetry.",
        "samples": [],
    }
    started = time.perf_counter()
    cells = [(model, mode) for mode in IMAGE_MODES for model in models]
    for repetition in range(1, repeats + 1):
        for model, mode in cells if repetition % 2 else reversed(cells):
            row = await sample(model, mode, repetition)
            report["samples"].append(row)
            print(json.dumps({key: row[key] for key in (
                "model", "image_mode", "repetition", "attempted", "valid", "correct"
            )}), flush=True)
    report["elapsed_ms"] = (time.perf_counter() - started) * 1000
    report["inference_calls"] = sum(row["attempted"] for row in report["samples"])
    report["summary"] = summarize(report["samples"])
    report["passed"] = all(row["correct"] and not row.get("error") for row in report["samples"])
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", nargs="+", default=["gpt-6-astra", "gpt-5.4-mini"])
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", type=Path, required=True, help="Untracked JSON report destination")
    args = parser.parse_args()
    # Only typed operational errors leave this process; SDK exceptions may contain private configuration.
    try:
        report = asyncio.run(benchmark(args.models, args.repeats))
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    except Exception as error:
        print(json.dumps({"passed": False, "error_type": type(error).__name__}), flush=True)
        return 1
    print(json.dumps({"summary": report["summary"], "passed": report["passed"]}, indent=2))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

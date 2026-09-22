"""Deterministic synthetic-screen guidance. No model, network, or tool execution."""

from src.models import GuidanceResult, Observation, PlanInput, Target, TaskProgress, plan_for


async def get_guidance(
    prompt: str, observation: Observation, *, task: TaskProgress | None = None, plan: bool = False,
) -> GuidanceResult:
    """Provider seam: validated observation in, structured guidance out; no side effects."""
    if plan:
        steps = []
        boundary = {
            "kind": "observation", "reason": "The deterministic demo needs its supported local resource.",
            "needed": "Select and approve the MSGuide Demo window.",
        }
        if observation.application == "MSGuide Demo":
            labels = ["View logs", "Open troubleshooting", "Mark resolved"]
            start = (3 if "Issue resolved" in observation.ocrText else
                     2 if "Check compiler errors and missing dependencies" in observation.ocrText else
                     1 if "Build failed: exit code 1" in observation.ocrText else 0)
            for label in labels[start:]:
                steps.append({
                    "kind": "action", "instruction": f"Demo: select {label}.",
                    "intent": {"role": "button", "label": label, "action": "invoke",
                               "frameworkId": "WPF"},
                })
            boundary = {
                "kind": "completion_candidate", "reason": "Review the demo's Issue resolved state; goal completion is not independently verified.",
                "needed": "",
            }
        return GuidanceResult(
            status="next_step", instruction="Demo plan segment; local checks are required before every action.",
            plan=plan_for(PlanInput.model_validate({"steps": steps, "boundary": boundary}), observation),
        )
    clarification = GuidanceResult(
        status="clarification",
        instruction="Demo only: share a fresh MSGuide Demo screen with a clearly identified workflow button.",
    )
    if observation.application != "MSGuide Demo":
        return clarification
    text = observation.ocrText
    if "Issue resolved" in text:
        return GuidanceResult(status="completed", instruction="Demo workflow completed: the screen reports Issue resolved.")
    if "Check compiler errors and missing dependencies" in text:
        label = "Mark resolved"
    elif "Build failed: exit code 1" in text:
        label = "Open troubleshooting"
    else:
        label = "View logs"
    matches = [element for element in observation.elements
               if element.role.casefold() == "button" and element.label == label and element.confidence >= 0.8]
    if len(matches) != 1:
        return clarification
    element = matches[0]
    return GuidanceResult(
        status="next_step", instruction=f"Demo: select {label} yourself, then check the screen again.",
        target=Target(
            label=label,
            box=element.box,
            confidence=element.confidence,
            processId=element.processId,
            targetId=element.targetId,
            automationId=element.automationId,
            frameworkId=element.frameworkId,
            isEnabled=element.isEnabled,
            isOffscreen=element.isOffscreen,
            toggleState=element.toggleState,
            action=element.action,
            **({"controlId": element.controlId} if element.controlId is not None else {}),
        ),
    )
"""Deterministic synthetic-screen guidance. No model, network, or tool execution."""

from src.models import GuidanceResult, Observation, Target


async def get_guidance(prompt: str, observation: Observation) -> GuidanceResult:
    """Provider seam: validated observation in, structured guidance out; no side effects."""
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
        target=Target(label=label, box=element.box, confidence=element.confidence),
    )
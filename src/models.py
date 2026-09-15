"""Bounded local-demo contracts. Screen evidence is data, never authority."""

from datetime import datetime, timezone
from enum import Enum
from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, StrictBool, field_validator, model_validator

from src.images import MAX_BASE64_CHARS, sanitize_png


class CaptureType(str, Enum):
    SELECTED_REGION = "selectedRegion"
    ACTIVE_WINDOW = "activeWindow"
    TEXT_SELECTION = "textSelection"


class SensitivityLevel(str, Enum):
    PUBLIC = "public"
    INTERNAL = "internal"
    CONFIDENTIAL = "confidential"
    RESTRICTED = "restricted"


class RequestType(str, Enum):
    EXPLAIN = "explain"
    RETRIEVE = "retrieve"
    GUIDE = "guide"
    ACTION = "action"


class ActionRiskLevel(str, Enum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    BLOCKED = "blocked"


class Contract(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)


Identifier = Annotated[str, Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9_.:-]+$")]
Prompt = Annotated[str, Field(min_length=1, max_length=4000)]
Text = Annotated[str, Field(max_length=16000)]
Label = Annotated[str, Field(min_length=1, max_length=256)]
Unit = Annotated[float, Field(strict=True, ge=0, le=1)]


def utc_timestamp(value: datetime) -> datetime:
    if value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("An explicit UTC offset is required")
    return value.astimezone(timezone.utc)


def bounded_box(box):
    x, y, width, height = box
    if width <= 0 or height <= 0 or x + width > 1 or y + height > 1:
        raise ValueError("Box must be nonempty and entirely inside the observation")
    return box


class UIElement(Contract):
    role: Annotated[str, Field(min_length=1, max_length=64)]
    label: Label
    box: tuple[Unit, Unit, Unit, Unit]
    confidence: Unit

    _box = field_validator("box")(bounded_box)


class Observation(Contract):
    id: Identifier
    windowId: Identifier
    application: Label
    capturedAt: datetime
    width: Annotated[int, Field(strict=True, ge=1, le=16384)]
    height: Annotated[int, Field(strict=True, ge=1, le=16384)]
    ocrText: Text
    elements: Annotated[list[UIElement], Field(max_length=200)]
    imageBase64: Annotated[str, Field(strict=True, max_length=MAX_BASE64_CHARS)] | None = None

    _utc = field_validator("capturedAt")(utc_timestamp)

    @model_validator(mode="after")
    def validated_image_only(self):
        if self.imageBase64 is not None:
            self.imageBase64 = sanitize_png(self.imageBase64, self.width, self.height)
        return self


class GuidanceRequest(Contract):
    sessionId: Identifier
    prompt: Prompt
    consent: StrictBool
    observation: Observation


class Citation(Contract):
    source: Annotated[str, Field(max_length=2048, pattern=r"^https://")]
    title: Label


class Target(Contract):
    label: Label
    box: tuple[Unit, Unit, Unit, Unit]
    confidence: Annotated[float, Field(strict=True, ge=0.8, le=1)]

    _box = field_validator("box")(bounded_box)


class GuidanceResult(Contract):
    instruction: Annotated[str, Field(min_length=1, max_length=4000)]
    status: Literal["next_step", "clarification", "completed"]
    target: Target | None = None
    citations: Annotated[list[Citation], Field(max_length=20)] = Field(default_factory=list)
    mode: Literal["demo", "model"] = "demo"

    @model_validator(mode="after")
    def target_matches_status(self):
        if self.status != "next_step" and self.target is not None:
            raise ValueError("Only next_step can have a target")
        return self


class GuidanceResponse(GuidanceResult):
    correlationId: Identifier
    observationId: Identifier
    windowId: Identifier


class ContextEnvelope(Contract):
    sessionId: Identifier
    captureType: CaptureType
    application: Label
    ocrText: Text
    elements: Annotated[list[UIElement], Field(max_length=200)]
    imageRef: Annotated[str, Field(max_length=2048)] = ""
    sensitivity: SensitivityLevel = SensitivityLevel.PUBLIC
    timestamp: datetime

    _utc = field_validator("timestamp")(utc_timestamp)


class AssistRequest(Contract):
    sessionId: Identifier
    prompt: Prompt
    context: ContextEnvelope | None = None
    responseModes: Annotated[list[Literal["text", "overlay", "voice"]], Field(max_length=3)] = Field(default_factory=lambda: ["text"])


class PreviewRequest(Contract):
    tool: Annotated[str, Field(min_length=1, max_length=64)]
    parameters: dict[str, Any]


class WorkItemParameters(Contract):
    title: Annotated[str, Field(min_length=1, max_length=256)]
    description: Annotated[str, Field(max_length=4000)] = ""


class LogParameters(Contract):
    application: Label = "MSGuide Demo"


class ExecuteRequest(Contract):
    grantToken: Annotated[str, Field(min_length=1, max_length=256)]


class RetrievedPassage(Contract):
    content: str
    sourceUri: str
    title: str
    modifiedTime: datetime
    classification: SensitivityLevel
    authorizationEvidence: str


class ActionPreview(Contract):
    id: str
    tool: str
    parameters: dict[str, Any]
    effects: str
    scopes: list[str]
    riskLevel: ActionRiskLevel
    expiryTime: datetime
    estimatedDuration: int


class AuditEvent(Contract):
    correlationId: str
    timestamp: datetime
    outcome: str

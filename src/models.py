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
EvidenceId = Annotated[str, Field(strict=True, min_length=1, max_length=128,
                                  pattern=r"^[A-Za-z0-9_.:-]+$")]
EvidenceName = Annotated[str, Field(strict=True, min_length=1, max_length=256)]


class ToggleState(str, Enum):
    OFF = "off"
    ON = "on"
    INDETERMINATE = "indeterminate"


class CameraRecoveryState(str, Enum):
    START = "start"
    TEAMS_PREJOIN_OBSERVED = "teams_prejoin_observed"
    CAMERA_BLOCK_CONFIRMED = "camera_block_confirmed"
    CAMERA_SETTINGS_OPEN = "camera_settings_open"
    APPLICABLE_PERMISSION_OFF = "applicable_permission_off"
    USER_ACTION_REQUIRED = "user_action_required"
    APPLICABLE_PERMISSION_ON = "applicable_permission_on"
    RETURN_TO_TEAMS = "return_to_teams"
    CAMERA_READY_VERIFIED = "camera_ready_verified"
    UNSUPPORTED = "unsupported"
    ADMIN_MANAGED = "admin_managed"
    AMBIGUOUS = "ambiguous"


class CameraEvidenceKind(str, Enum):
    TEAMS_PREJOIN_SURFACE = "teams_prejoin_surface"
    CAMERA_TOGGLE_OFF = "camera_toggle_off"
    CAMERA_TOGGLE_ON = "camera_toggle_on"
    CAMERA_BLOCK_INDICATOR = "camera_block_indicator"
    CAMERA_SETTINGS_SURFACE = "camera_settings_surface"
    APPLICABLE_PERMISSION_OFF = "applicable_permission_off"
    APPLICABLE_PERMISSION_ON = "applicable_permission_on"
    PERMISSION_TOGGLE_ENABLED = "permission_toggle_enabled"
    RETURNED_TO_TEAMS = "returned_to_teams"
    LOCAL_CAMERA_VERIFIER = "local_camera_verifier"


class CameraPermissionState(str, Enum):
    UNKNOWN = "unknown"
    OFF = "off"
    ON = "on"
    MANAGED = "managed"


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
    targetId: EvidenceId | None = None
    automationId: EvidenceName | None = None
    frameworkId: EvidenceName | None = None
    isEnabled: StrictBool | None = None
    toggleState: ToggleState | None = None

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
    def validate_observation(self):
        target_ids = [element.targetId for element in self.elements if element.targetId is not None]
        if len(target_ids) != len(set(target_ids)):
            raise ValueError("Element target IDs must be unique within an observation")
        if self.imageBase64 is not None:
            self.imageBase64 = sanitize_png(self.imageBase64, self.width, self.height)
        return self


class CameraReadyVerification(Contract):
    kind: Literal["localCameraReady"]
    source: Literal["desktopLocalCameraVerifier"]
    evidenceId: EvidenceId
    sessionId: Identifier
    observationId: Identifier
    windowId: Identifier
    capturedAt: datetime
    cameraActive: StrictBool
    framesObserved: Annotated[int, Field(strict=True, ge=2, le=120)]

    _utc = field_validator("capturedAt")(utc_timestamp)

    @model_validator(mode="after")
    def camera_must_be_active(self):
        if not self.cameraActive:
            raise ValueError("Camera readiness requires an active local camera signal")
        return self


class CameraRecoveryRequest(Contract):
    profile: Literal["teams-camera-recovery-win11-24h2-en-US-fixture-v1"]
    verification: CameraReadyVerification | None = None


class GuidanceRequest(Contract):
    sessionId: Identifier
    prompt: Prompt
    consent: StrictBool
    observation: Observation
    cameraRecovery: CameraRecoveryRequest | None = None

    @model_validator(mode="after")
    def bind_camera_verification(self):
        if self.cameraRecovery is None or self.cameraRecovery.verification is None:
            return self
        verification = self.cameraRecovery.verification
        if (verification.sessionId != self.sessionId
                or verification.observationId != self.observation.id
                or verification.windowId != self.observation.windowId):
            raise ValueError("Camera verification must match the request session and observation")
        return self


class Citation(Contract):
    source: Annotated[str, Field(max_length=2048, pattern=r"^https://")]
    title: Label


class Target(Contract):
    label: Label
    box: tuple[Unit, Unit, Unit, Unit]
    confidence: Annotated[float, Field(strict=True, ge=0.8, le=1)]
    targetId: EvidenceId | None = None
    automationId: EvidenceName | None = None
    frameworkId: EvidenceName | None = None
    isEnabled: StrictBool | None = None
    toggleState: ToggleState | None = None

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


class CameraRecoveryResponse(Contract):
    profile: Literal["teams-camera-recovery-win11-24h2-en-US-fixture-v1"]
    fixtureSupported: Literal[True] = True
    state: CameraRecoveryState
    evidence: Annotated[list[CameraEvidenceKind], Field(max_length=12)]
    permissionState: CameraPermissionState = CameraPermissionState.UNKNOWN
    verificationRequired: StrictBool


class GuidanceResponse(GuidanceResult):
    correlationId: Identifier
    observationId: Identifier
    windowId: Identifier
    cameraRecovery: CameraRecoveryResponse | None = None

    @model_validator(mode="after")
    def camera_completion_is_verifier_owned(self):
        if self.cameraRecovery is None:
            return self
        ready = self.cameraRecovery.state == CameraRecoveryState.CAMERA_READY_VERIFIED
        if ready != (self.status == "completed") or ready == self.cameraRecovery.verificationRequired:
            raise ValueError("Camera completion requires accepted readiness evidence")
        return self


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

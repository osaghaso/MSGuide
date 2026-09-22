"""Bounded local-demo contracts. Screen evidence is data, never authority."""

from datetime import datetime, timezone
from enum import Enum
from typing import Annotated, Any, Literal
from hashlib import sha256
from uuid import uuid4

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
OptionalEvidenceName = Annotated[str, Field(strict=True, max_length=256)]
UIAction = Literal["invoke", "toggle", "select", "expand", "collapse", "set_value", "scroll"]
ScrollDirection = Literal["up", "down", "left", "right"]
InputValue = Annotated[str, Field(strict=True, max_length=1000)]
ValueHash = Annotated[str, Field(strict=True, pattern=r"^[a-f0-9]{64}$")]
TaskId = Annotated[str, Field(strict=True, pattern=r"^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$")]
OBSERVATION_MAX_AGE_SECONDS = 60.0
MAX_PLAN_STEPS = 32
EMPTY_VALUE_HASH = sha256(b"").hexdigest()


def guidance_seconds(captured_at: datetime, current: datetime, headroom: float) -> float:
    # Keep the timestamp of the earliest evidence, not the end of capture/encoding.
    age = max(0.0, (current - captured_at).total_seconds())
    return max(0.0, OBSERVATION_MAX_AGE_SECONDS - age - headroom)


class ToggleState(str, Enum):
    OFF = "off"
    ON = "on"
    INDETERMINATE = "indeterminate"


class TeamsCameraSessionState(str, Enum):
    NOT_INITIALIZED = "notInitialized"
    ACTIVE = "active"
    REINITIALIZING = "reinitializing"
    REINITIALIZED = "reinitialized"
    UNKNOWN = "unknown"


class CameraRecoveryState(str, Enum):
    START = "start"
    TEAMS_PREJOIN_OBSERVED = "teams_prejoin_observed"
    CAMERA_BLOCK_CONFIRMED = "camera_block_confirmed"
    TEAMS_SETTINGS_MENU_OPEN = "teams_settings_menu_open"
    TEAMS_DEVICES_OPEN = "teams_devices_open"
    CAMERA_SETTINGS_OPEN = "camera_settings_open"
    SYSTEM_CAMERA_SETTINGS_UNVERIFIED = "system_camera_settings_unverified"
    SYSTEM_CAMERA_SETTINGS_UNINSPECTABLE = "system_camera_settings_uninspectable"
    APPLICABLE_PERMISSION_OFF = "applicable_permission_off"
    USER_ACTION_REQUIRED = "user_action_required"
    APPLICABLE_PERMISSION_ON = "applicable_permission_on"
    CAMERA_REINITIALIZATION_REQUIRED = "camera_reinitialization_required"
    RETURN_TO_TEAMS = "return_to_teams"
    CAMERA_READY_VERIFIED = "camera_ready_verified"
    UNSUPPORTED = "unsupported"
    ADMIN_MANAGED = "admin_managed"
    AMBIGUOUS = "ambiguous"


class CameraEvidenceKind(str, Enum):
    TEAMS_SELECTED_WINDOW = "teams_selected_window"
    TEAMS_PREJOIN_SURFACE = "teams_prejoin_surface"
    TEAMS_MORE_OPTIONS = "teams_more_options"
    TEAMS_SETTINGS_ITEM = "teams_settings_item"
    TEAMS_DEVICES_TAB = "teams_devices_tab"
    TEAMS_DEVICES_SURFACE = "teams_devices_surface"
    TEAMS_CAMERA_SELECTOR = "teams_camera_selector"
    OPEN_SYSTEM_CAMERA_SETTINGS = "open_system_camera_settings"
    PINNED_CAMERA_SETTINGS_URI = "pinned_camera_settings_uri"
    CAMERA_TOGGLE_OFF = "camera_toggle_off"
    CAMERA_TOGGLE_ON = "camera_toggle_on"
    CAMERA_BLOCK_INDICATOR = "camera_block_indicator"
    CAMERA_SETTINGS_SURFACE = "camera_settings_surface"
    APPLICABLE_PERMISSION_OFF = "applicable_permission_off"
    APPLICABLE_PERMISSION_ON = "applicable_permission_on"
    PERMISSION_TOGGLE_ENABLED = "permission_toggle_enabled"
    RETURNED_TO_TEAMS = "returned_to_teams"
    LOCAL_CAMERA_VERIFIER = "local_camera_verifier"
    SYSTEM_CAMERA_SETTINGS_SURFACE = "system_camera_settings_surface"
    UIA_NO_DESCENDANTS = "uia_no_descendants"
    CAMERA_SETTINGS_PAGE_VERIFIED = "camera_settings_page_verified"
    SYSTEM_CAMERA_GLOBAL_ON = "system_camera_global_on"
    USER_CAMERA_GLOBAL_ON = "user_camera_global_on"
    TEAMS_CAMERA_PERMISSION = "teams_camera_permission"
    PERMISSION_PREPARED_BEFORE_CAMERA = "permission_prepared_before_camera"
    CAMERA_REINITIALIZED = "camera_reinitialized"


class CameraPermissionState(str, Enum):
    UNKNOWN = "unknown"
    OFF = "off"
    ON = "on"
    MANAGED = "managed"


class CameraEvidenceBasis(str, Enum):
    FIXTURE = "fixture"
    LIVE_PROBE = "liveProbe"


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
    processId: Annotated[int, Field(strict=True, ge=1, le=4_294_967_295)] | None = None
    targetId: EvidenceId | None = None
    controlId: EvidenceId | None = None
    automationId: OptionalEvidenceName | None = None
    frameworkId: OptionalEvidenceName | None = None
    isEnabled: StrictBool | None = None
    isOffscreen: StrictBool | None = None
    targetable: StrictBool | None = None
    toggleState: ToggleState | None = None
    helpText: Annotated[str, Field(strict=True, max_length=256)] | None = None
    itemStatus: Annotated[str, Field(strict=True, max_length=128)] | None = None
    action: UIAction | None = None
    isPassword: StrictBool | None = None
    isReadOnly: StrictBool | None = None
    valueHash: ValueHash | None = None
    valueLength: Annotated[int, Field(strict=True, ge=0, le=1000)] | None = None
    isSelected: StrictBool | None = None
    scrollDirections: Annotated[list[ScrollDirection], Field(max_length=4)] = Field(default_factory=list)
    horizontalScrollPercent: Annotated[float, Field(ge=-1, le=100)] | None = None
    verticalScrollPercent: Annotated[float, Field(ge=-1, le=100)] | None = None

    _box = field_validator("box")(bounded_box)


def executable_element(element: UIElement) -> bool:
    if (element.action is None or element.targetId is None
            or element.confidence < 0.8 or element.isEnabled is not True
            or element.isOffscreen is not False or element.targetable is not True
            or element.isPassword is True):
        return False
    if element.action == "set_value":
        return (element.isPassword is False and element.isReadOnly is False
                and element.valueHash is not None and element.valueLength is not None)
    if element.action == "scroll":
        return bool(element.scrollDirections)
    return element.action != "toggle" or element.toggleState in {ToggleState.OFF, ToggleState.ON}

class PlanIntent(Contract):
    role: Annotated[str, Field(strict=True, min_length=1, max_length=64)]
    label: Label
    action: UIAction
    automationId: OptionalEvidenceName | None = None
    frameworkId: OptionalEvidenceName | None = None
    toggleState: ToggleState | None = None
    isSelected: StrictBool | None = None

    @model_validator(mode="after")
    def expected_state(self):
        if (not self.role.strip() or not self.label.strip()
                or (self.action == "toggle"
                    and self.toggleState not in {ToggleState.OFF, ToggleState.ON})
                or (self.action != "toggle" and self.toggleState is not None)
                or (self.action == "select" and self.isSelected is not False)
                or (self.action != "select" and self.isSelected is not None)):
            raise ValueError("Plan intent requires exact supported preconditions")
        return self


class PlanBoundary(Contract):
    kind: Literal[
        "completion_candidate", "resource", "needs_input", "permission",
        "observation", "unsupported", "plan_limit",
    ]
    reason: Annotated[str, Field(strict=True, min_length=1, max_length=500)]
    needed: Annotated[str, Field(strict=True, max_length=1000)] = ""

    @model_validator(mode="after")
    def explicit_boundary(self):
        if not self.reason.strip() or self.kind != "completion_candidate" and not self.needed.strip():
            raise ValueError("A boundary must say what is needed next")
        return self


class PlanStep(Contract):
    kind: Literal["action", "manual"]
    instruction: Annotated[str, Field(strict=True, min_length=1, max_length=500)]
    intent: PlanIntent | None = None
    controlId: EvidenceId | None = None
    value: InputValue | None = None
    scrollDirection: ScrollDirection | None = None
    valueHash: ValueHash | None = None

    @model_validator(mode="after")
    def action_shape(self):
        if not self.instruction.strip():
            raise ValueError("A plan step needs a description")
        if self.kind == "manual":
            if any(value is not None for value in (
                self.intent, self.controlId, self.value, self.scrollDirection, self.valueHash,
            )):
                raise ValueError("Manual steps cannot authorize actions")
            return self
        if self.intent is None:
            raise ValueError("Action steps require an exact target intent")
        action = self.intent.action
        if ((action == "set_value") != (self.value is not None)
                or (action == "set_value") != (self.valueHash is not None)
                or (action == "scroll") != (self.scrollDirection is not None)
                or (self.value is not None
                    and any(ord(c) < 32 and c not in "\r\n\t" for c in self.value))
                or (action == "set_value" and self.controlId is None
                    and self.valueHash != EMPTY_VALUE_HASH)):
            raise ValueError("Plan parameters must match the action; deferred writes require an empty field")
        return self


def _bounded_plan(steps, boundary):
    if (len(steps) == MAX_PLAN_STEPS and boundary.kind == "completion_candidate"
            or any(step.kind == "manual" for step in steps[:-1])
            or steps and steps[-1].kind == "manual" and boundary.kind == "completion_candidate"):
        raise ValueError("Plan limits and manual work require an explicit non-completion boundary")


class PlanSegment(Contract):
    planId: TaskId
    windowId: Identifier
    resourceId: EvidenceId | None = None
    steps: Annotated[list[PlanStep], Field(max_length=MAX_PLAN_STEPS)]
    boundary: PlanBoundary

    @model_validator(mode="after")
    def bounded_segment(self):
        _bounded_plan(self.steps, self.boundary)
        return self


class PlanStepInput(Contract):
    kind: Literal["action", "manual"]
    instruction: Annotated[str, Field(strict=True, min_length=1, max_length=500)]
    targetId: Identifier | None = None
    targetIndex: Annotated[int, Field(strict=True, ge=0, le=199)] | None = None
    intent: PlanIntent | None = None
    value: InputValue | None = None
    scrollDirection: ScrollDirection | None = None

    @model_validator(mode="after")
    def target_shape(self):
        references = sum(value is not None for value in (self.targetId, self.targetIndex, self.intent))
        if (not self.instruction.strip()
                or self.kind == "action" and references != 1
                or self.kind == "manual" and (
                    references != 0 or self.value is not None or self.scrollDirection is not None)):
            raise ValueError("A model plan step requires exactly one target reference or intent")
        return self


class PlanInput(Contract):
    steps: Annotated[list[PlanStepInput], Field(max_length=MAX_PLAN_STEPS)]
    boundary: PlanBoundary

    @model_validator(mode="after")
    def bounded_segment(self):
        _bounded_plan(self.steps, self.boundary)
        return self


class Observation(Contract):
    id: Identifier
    windowId: Identifier
    application: Label
    rootProcessId: Annotated[int, Field(strict=True, ge=1, le=4_294_967_295)] | None = None
    teamsCameraSessionState: TeamsCameraSessionState | None = None
    capturedAt: datetime
    width: Annotated[int, Field(strict=True, ge=1, le=16384)]
    height: Annotated[int, Field(strict=True, ge=1, le=16384)]
    ocrText: Text
    elements: Annotated[list[UIElement], Field(max_length=200)]
    imageBase64: Annotated[str, Field(strict=True, max_length=MAX_BASE64_CHARS)] | None = None
    automationComplete: StrictBool = True
    resourceId: EvidenceId | None = None

    _utc = field_validator("capturedAt")(utc_timestamp)

    @model_validator(mode="after")
    def validate_observation(self):
        target_ids = [element.targetId for element in self.elements if element.targetId is not None]
        if len(target_ids) != len(set(target_ids)):
            raise ValueError("Element target IDs must be unique within an observation")
        control_ids = [element.controlId for element in self.elements if element.controlId is not None]
        if len(control_ids) != len(set(control_ids)):
            raise ValueError("Logical control identities must be unique within an observation")
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
    reinitializationMethod: Literal[
        "prejoinReopened",
        "teamsRelaunched",
        "cameraDeviceReinitialized",
    ] | None = None

    _utc = field_validator("capturedAt")(utc_timestamp)

    @model_validator(mode="after")
    def camera_must_be_active(self):
        if not self.cameraActive:
            raise ValueError("Camera readiness requires an active local camera signal")
        return self


class CameraRecoveryRequest(Contract):
    profile: Literal[
        "teams-camera-recovery-win11-24h2-en-US-fixture-v1",
        "teams-camera-recovery-new-teams-uia-probe-20260916-v1",
        "teams-camera-recovery-pinned-20260916-v2",
    ]
    verification: CameraReadyVerification | None = None


class TaskStep(Contract):
    step: Annotated[int, Field(strict=True, ge=1, le=10000)]
    observationId: Identifier
    afterObservationId: Identifier | None = None
    targetId: EvidenceId
    label: Label
    action: UIAction
    outcome: Literal["effect_observed", "screen_changed", "no_progress", "unknown", "not_invoked"]


class TaskProgress(Contract):
    taskId: TaskId
    step: Annotated[int, Field(strict=True, ge=1, le=10000)]
    status: Literal[
        "running", "checkpoint", "blocked", "needs_input", "review_required",
        "no_progress", "unknown", "cancelled", "failed",
    ] = "running"
    history: Annotated[list[TaskStep], Field(max_length=16)] = Field(default_factory=list)
    remainingWork: Annotated[str, Field(strict=True, max_length=1000)] = ""
    userInput: Annotated[str, Field(strict=True, max_length=1000)] = ""
    plan: PlanSegment | None = None
    planCursor: Annotated[int, Field(strict=True, ge=0, le=MAX_PLAN_STEPS)] = 0
    replanReason: Annotated[str, Field(strict=True, max_length=500)] = ""

    @model_validator(mode="after")
    def ordered_history(self):
        steps = [item.step for item in self.history]
        if steps != sorted(set(steps)) or any(step >= self.step for step in steps):
            raise ValueError("Task history must precede the current step in order")
        if self.planCursor > (len(self.plan.steps) if self.plan else 0):
            raise ValueError("Plan cursor must refer to the retained segment")
        return self


class GuidanceRequest(Contract):
    sessionId: Identifier
    prompt: Prompt
    consent: StrictBool
    observation: Observation
    cameraRecovery: CameraRecoveryRequest | None = None
    task: TaskProgress | None = None
    planSegments: StrictBool = False

    @model_validator(mode="after")
    def bind_camera_verification(self):
        if (self.task is not None or self.planSegments) and self.cameraRecovery is not None:
            raise ValueError("Generic task context cannot authorize camera recovery")
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
    processId: Annotated[int, Field(strict=True, ge=1, le=4_294_967_295)] | None = None
    targetId: EvidenceId | None = None
    controlId: EvidenceId | None = None
    automationId: EvidenceName | None = None
    frameworkId: EvidenceName | None = None
    isEnabled: StrictBool | None = None
    isOffscreen: StrictBool | None = None
    toggleState: ToggleState | None = None
    action: UIAction | None = None
    value: InputValue | None = None
    scrollDirection: ScrollDirection | None = None
    valueHash: ValueHash | None = None
    isSelected: StrictBool | None = None

    _box = field_validator("box")(bounded_box)

    @model_validator(mode="after")
    def semantic_input(self):
        if ((self.action == "set_value") != (self.value is not None)
                or (self.action == "scroll") != (self.scrollDirection is not None)
                or (self.action == "set_value" and self.valueHash is None)
                or (self.value is not None
                    and any(ord(c) < 32 and c not in "\r\n\t" for c in self.value))):
            raise ValueError("Explicit input must match the observed semantic action")
        return self


def target_for(element: UIElement, *, value=None, scroll_direction=None) -> Target:
    if element.action is not None and not executable_element(element):
        raise ValueError("Non-actionable target")
    if element.action == "scroll" and scroll_direction not in element.scrollDirections:
        raise ValueError("Unsupported scroll direction")
    return Target(
        **element.model_dump(include={
            "label", "box", "confidence", "processId", "targetId", "isEnabled",
            "isOffscreen", "toggleState", "action", "valueHash",
        }),
        **({"controlId": element.controlId} if element.controlId is not None else {}),
        **({"isSelected": element.isSelected} if element.isSelected is not None else {}),
        automationId=element.automationId or None,
        frameworkId=element.frameworkId or None,
        value=value,
        scrollDirection=scroll_direction,
    )

def intent_for(element: UIElement) -> PlanIntent:
    return PlanIntent(
        role=element.role, label=element.label, action=element.action,
        automationId=element.automationId, frameworkId=element.frameworkId,
        toggleState=element.toggleState if element.action == "toggle" else None,
        isSelected=element.isSelected if element.action == "select" else None,
    )


def validate_plan(plan: PlanSegment, observation: Observation) -> None:
    if plan.windowId != observation.windowId or plan.resourceId != observation.resourceId:
        raise ValueError("Plan scope does not match the approved observation")
    for step in plan.steps:
        if step.controlId is None:
            continue
        matches = [element for element in observation.elements if element.controlId == step.controlId]
        if (not observation.automationComplete or len(matches) != 1
                or not executable_element(matches[0]) or intent_for(matches[0]) != step.intent
                or step.valueHash != (matches[0].valueHash if matches[0].action == "set_value" else None)):
            raise ValueError("Observed plan target or preconditions do not match")
        target_for(matches[0], value=step.value, scroll_direction=step.scrollDirection)


def plan_for(value: PlanInput, observation: Observation,
             targets: dict[str, UIElement] | None = None) -> PlanSegment:
    steps = []
    for item in value.steps:
        if item.kind == "manual":
            steps.append(PlanStep(kind="manual", instruction=item.instruction))
            continue
        element = None
        if item.targetId is not None:
            if targets is None or item.targetId not in targets:
                raise ValueError("Unapproved opaque plan target")
            element = targets[item.targetId]
        elif item.targetIndex is not None:
            if item.targetIndex >= len(observation.elements):
                raise ValueError("Unknown plan target index")
            element = observation.elements[item.targetIndex]
            if targets is not None and element not in targets.values():
                raise ValueError("Unapproved plan target index")
        if element is not None:
            if not observation.automationComplete or not element.controlId or not executable_element(element):
                raise ValueError("Observed plan targets require complete stable grounding")
            intent = intent_for(element)
            value_hash = element.valueHash if element.action == "set_value" else None
        else:
            intent = item.intent
            value_hash = EMPTY_VALUE_HASH if intent.action == "set_value" else None
        steps.append(PlanStep(
            kind="action", instruction=item.instruction, intent=intent,
            controlId=element.controlId if element is not None else None,
            value=item.value, scrollDirection=item.scrollDirection, valueHash=value_hash,
        ))
    plan = PlanSegment(planId=str(uuid4()), windowId=observation.windowId,
                       resourceId=observation.resourceId, steps=steps, boundary=value.boundary)
    validate_plan(plan, observation)
    return plan


class GuidanceResult(Contract):
    instruction: Annotated[str, Field(min_length=1, max_length=4000)]
    status: Literal["next_step", "clarification", "completed", "blocked", "needs_input", "completion_candidate"]
    target: Target | None = None
    citations: Annotated[list[Citation], Field(max_length=20)] = Field(default_factory=list)
    mode: Literal["demo", "model"] = "demo"
    remainingWork: Annotated[str, Field(strict=True, max_length=1000)] | None = None
    plan: PlanSegment | None = None

    @model_validator(mode="after")
    def target_matches_status(self):
        if self.status != "next_step" and self.target is not None:
            raise ValueError("Only next_step can have a target")
        if self.plan is not None and (self.status != "next_step" or self.target is not None):
            raise ValueError("A plan is a next_step segment, not a second legacy target")
        return self


class CameraRecoveryResponse(Contract):
    profile: Literal[
        "teams-camera-recovery-win11-24h2-en-US-fixture-v1",
        "teams-camera-recovery-new-teams-uia-probe-20260916-v1",
        "teams-camera-recovery-pinned-20260916-v2",
    ]
    evidenceBasis: CameraEvidenceBasis
    fixtureSupported: StrictBool
    settingsUiaProven: StrictBool
    rawPixelEvidenceUsed: Literal[False] = False
    settingsLaunchUri: Literal["ms-settings:privacy-webcam"] | None = None
    state: CameraRecoveryState
    evidence: Annotated[list[CameraEvidenceKind], Field(max_length=12)]
    permissionState: CameraPermissionState = CameraPermissionState.UNKNOWN
    verificationRequired: StrictBool

    @model_validator(mode="after")
    def evidence_basis_matches_profile(self):
        fixture = self.profile.endswith("-fixture-v1")
        if (fixture != self.fixtureSupported
                or fixture != (self.evidenceBasis == CameraEvidenceBasis.FIXTURE)
                or self.settingsUiaProven != self.profile.endswith("-pinned-20260916-v2")):
            raise ValueError("Camera evidence basis must match the selected profile")
        if (self.settingsLaunchUri is not None
                and (not self.profile.endswith("-pinned-20260916-v2")
                     or self.state != CameraRecoveryState.TEAMS_DEVICES_OPEN)):
            raise ValueError("Camera settings launch URI is limited to the pinned Devices state")
        return self


class GuidanceResponse(GuidanceResult):
    correlationId: Identifier
    observationId: Identifier
    windowId: Identifier
    cameraRecovery: CameraRecoveryResponse | None = None
    taskId: TaskId | None = None
    step: Annotated[int, Field(strict=True, ge=1, le=10000)] | None = None

    @model_validator(mode="after")
    def camera_completion_is_verifier_owned(self):
        if self.cameraRecovery is None:
            return self
        if self.plan is not None:
            raise ValueError("Generic plans cannot authorize camera recovery")
        ready = self.cameraRecovery.state == CameraRecoveryState.CAMERA_READY_VERIFIED
        if (ready != (self.status == "completed")
                or ready == self.cameraRecovery.verificationRequired
                or (ready and self.cameraRecovery.profile
                    == "teams-camera-recovery-new-teams-uia-probe-20260916-v1")):
            raise ValueError("Camera completion requires accepted readiness evidence")
        if self.cameraRecovery.settingsLaunchUri is not None and self.status != "next_step":
            raise ValueError("Camera settings launch guidance must be a next step")
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

"""Deterministic, explicitly profiled Teams camera recovery evidence engine."""

from __future__ import annotations

import base64
from dataclasses import dataclass
from datetime import datetime
import hashlib
import hmac
import json

from src.models import (
    CameraEvidenceBasis,
    CameraEvidenceKind,
    CameraPermissionState,
    CameraReadyVerification,
    CameraRecoveryResponse,
    CameraRecoveryState,
    GuidanceResult,
    Observation,
    Target,
    ToggleState,
    UIElement,
)


FIXTURE_PROFILE = "teams-camera-recovery-win11-24h2-en-US-fixture-v1"
LIVE_PROFILE = "teams-camera-recovery-new-teams-uia-probe-20260916-v1"


class CameraRecoveryError(ValueError):
    """The supplied evidence cannot safely advance the deterministic scenario."""


@dataclass(frozen=True)
class ElementPredicate:
    automation_ids: tuple[str, ...]
    labels: tuple[str, ...]
    roles: tuple[str, ...]
    frameworks: tuple[str, ...]
    label_fallback_with_automation: bool = False


@dataclass(frozen=True)
class EvidenceProfile:
    name: str
    teams_applications: tuple[str, ...]
    settings_applications: tuple[str, ...]
    teams_camera: ElementPredicate
    teams_blocked: ElementPredicate
    teams_open_settings: ElementPredicate
    settings_permission: ElementPredicate


SUPPORTED_PROFILE = EvidenceProfile(
    name=FIXTURE_PROFILE,
    teams_applications=("Microsoft Teams",),
    settings_applications=("Settings", "Windows Settings"),
    teams_camera=ElementPredicate(
        automation_ids=("teams-prejoin-camera-toggle",),
        labels=("Camera",),
        roles=("button", "togglebutton"),
        frameworks=("chrome", "webview2"),
    ),
    teams_blocked=ElementPredicate(
        automation_ids=("teams-camera-permission-blocked",),
        labels=("Your camera is blocked",),
        roles=("text", "status"),
        frameworks=("chrome", "webview2"),
    ),
    teams_open_settings=ElementPredicate(
        automation_ids=("teams-open-camera-settings",),
        labels=("Open camera settings",),
        roles=("button", "hyperlink", "link"),
        frameworks=("chrome", "webview2"),
    ),
    settings_permission=ElementPredicate(
        automation_ids=("windows-camera-app-permission-toggle",),
        labels=("Let desktop apps access your camera",),
        roles=("checkbox", "switch", "togglebutton"),
        frameworks=("xaml",),
    ),
)

LIVE_MORE_OPTIONS = ElementPredicate(
    automation_ids=("more-options-header",),
    labels=("Settings and more",),
    roles=("button",),
    frameworks=("chrome", "webview2"),
)
LIVE_SETTINGS_ITEM = ElementPredicate(
    automation_ids=("settings", "settings-menu-item"),
    labels=("Settings",),
    roles=("button", "menuitem"),
    frameworks=("chrome", "webview2"),
    label_fallback_with_automation=True,
)
LIVE_DEVICES_TAB = ElementPredicate(
    automation_ids=("devices",),
    labels=("Devices",),
    roles=("tabitem",),
    frameworks=("chrome", "webview2"),
    label_fallback_with_automation=True,
)
LIVE_DEVICES_MARKER = ElementPredicate(
    automation_ids=("audiosettings", "videosettings"),
    labels=(),
    roles=("custom", "group", "pane", "section", "text"),
    frameworks=("chrome", "webview2"),
)
LIVE_OPEN_CAMERA_SETTINGS = ElementPredicate(
    automation_ids=("open_camera_settings",),
    labels=("Open camera settings",),
    roles=("button", "hyperlink", "link"),
    frameworks=("chrome", "webview2"),
)


@dataclass
class RecoveryRecord:
    block_confirmed: bool = False
    permission_off_observed: bool = False
    permission_on_observed: bool = False
    last_state: CameraRecoveryState = CameraRecoveryState.START
    last_observation_id: str | None = None
    last_captured_at: datetime | None = None
    last_fingerprint: str | None = None
    last_target_id: str | None = None
    last_target_digest: bytes | None = None


@dataclass(frozen=True)
class CameraDecision:
    guidance: GuidanceResult
    recovery: CameraRecoveryResponse


def _normalized(value: str | None) -> str:
    return value.casefold() if value is not None else ""


def _rank(element: UIElement, predicate: ElementPredicate) -> int:
    if (element.confidence < 0.8
            or _normalized(element.role) not in predicate.roles
            or _normalized(element.frameworkId) not in predicate.frameworks):
        return 0
    if element.automationId is not None:
        if _normalized(element.automationId) in predicate.automation_ids:
            return 100
        if not predicate.label_fallback_with_automation:
            return 0
    if element.label in predicate.labels:
        return 50
    return 0


def _candidate(observation: Observation, predicate: ElementPredicate) -> tuple[str, int | None]:
    ranked = [(index, _rank(element, predicate)) for index, element in enumerate(observation.elements)]
    ranked = [(index, score) for index, score in ranked if score]
    if not ranked:
        return "missing", None
    best = max(score for _, score in ranked)
    winners = [index for index, score in ranked if score == best]
    if len(winners) != 1:
        return "ambiguous", None
    return "matched", winners[0]


def _has_match(observation: Observation, predicate: ElementPredicate) -> bool:
    return any(_rank(element, predicate) for element in observation.elements)


class CameraRecoveryEngine:
    """Single-worker state machine; observations remain the authority for every transition."""

    def __init__(self, target_key: bytes):
        if len(target_key) < 32:
            raise ValueError("Target binding key is too short")
        self._target_key = target_key
        self._records: dict[str, RecoveryRecord] = {}

    def discard(self, session_id: str) -> None:
        self._records.pop(session_id, None)

    def clear(self) -> None:
        self._records.clear()

    def _record(self, session_id: str) -> RecoveryRecord:
        return self._records.setdefault(session_id, RecoveryRecord())

    @staticmethod
    def _fingerprint(observation: Observation) -> str:
        evidence = observation.model_dump(mode="json", exclude={"imageBase64"})
        encoded = json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode()
        return hashlib.sha256(encoded).hexdigest()

    def _accept_observation(self, record: RecoveryRecord, observation: Observation) -> None:
        fingerprint = self._fingerprint(observation)
        if record.last_captured_at is not None and observation.capturedAt < record.last_captured_at:
            raise CameraRecoveryError("Camera recovery observations must be processed in capture order")
        if record.last_observation_id == observation.id:
            if (record.last_captured_at != observation.capturedAt
                    or record.last_fingerprint != fingerprint):
                raise CameraRecoveryError("An observation ID cannot be reused with changed evidence")
            return
        record.last_observation_id = observation.id
        record.last_captured_at = observation.capturedAt
        record.last_fingerprint = fingerprint

    def _binding_digest(self, session_id: str, observation: Observation, index: int) -> bytes:
        element = observation.elements[index]
        evidence = {
            "sessionId": session_id,
            "observationId": observation.id,
            "windowId": observation.windowId,
            "capturedAt": observation.capturedAt.isoformat(),
            "index": index,
            "element": element.model_dump(mode="json"),
        }
        return hmac.new(
            self._target_key,
            json.dumps(evidence, sort_keys=True, separators=(",", ":")).encode(),
            hashlib.sha256,
        ).digest()

    def _binding_id(self, session_id: str, observation: Observation, index: int) -> str:
        element = observation.elements[index]
        if element.targetId is not None:
            return element.targetId
        digest = self._binding_digest(session_id, observation, index)
        return "tgt." + base64.urlsafe_b64encode(digest).decode().rstrip("=")

    def _target(self, session_id: str, observation: Observation, index: int) -> Target:
        element = observation.elements[index]
        target_id = self._binding_id(session_id, observation, index)
        record = self._record(session_id)
        record.last_target_id = target_id
        record.last_target_digest = self._binding_digest(session_id, observation, index)
        return Target(
            targetId=target_id,
            label=element.label,
            box=element.box,
            confidence=element.confidence,
            processId=element.processId,
            automationId=element.automationId,
            frameworkId=element.frameworkId,
            isEnabled=element.isEnabled,
            toggleState=element.toggleState,
        )

    def target_matches(self, session_id: str, observation: Observation, target: Target) -> bool:
        if target.targetId is None:
            return False
        record = self._records.get(session_id)
        if record is None or record.last_target_id != target.targetId:
            return False
        matches = []
        for index, element in enumerate(observation.elements):
            if self._binding_id(session_id, observation, index) != target.targetId:
                continue
            matches.append(
                self._binding_digest(session_id, observation, index) == record.last_target_digest
                and
                element.label == target.label
                and element.box == target.box
                and element.confidence >= target.confidence
                and element.processId == target.processId
                and element.automationId == target.automationId
                and element.frameworkId == target.frameworkId
                and element.isEnabled == target.isEnabled
                and element.toggleState == target.toggleState
            )
        return matches == [True]

    @staticmethod
    def _recovery(
        state: CameraRecoveryState,
        evidence: list[CameraEvidenceKind],
        permission: CameraPermissionState = CameraPermissionState.UNKNOWN,
        *,
        verified: bool = False,
        profile: str = FIXTURE_PROFILE,
    ) -> CameraRecoveryResponse:
        return CameraRecoveryResponse(
            profile=profile,
            evidenceBasis=(CameraEvidenceBasis.FIXTURE
                           if profile == FIXTURE_PROFILE else CameraEvidenceBasis.LIVE_PROBE),
            fixtureSupported=profile == FIXTURE_PROFILE,
            settingsUiaProven=False,
            state=state,
            evidence=evidence,
            permissionState=permission,
            verificationRequired=not verified,
        )

    @staticmethod
    def _clarification(
        state: CameraRecoveryState,
        instruction: str,
        evidence: list[CameraEvidenceKind],
        permission: CameraPermissionState = CameraPermissionState.UNKNOWN,
        *,
        profile: str = FIXTURE_PROFILE,
    ) -> CameraDecision:
        return CameraDecision(
            GuidanceResult(status="clarification", instruction=instruction),
            CameraRecoveryEngine._recovery(state, evidence, permission, profile=profile),
        )

    def _next_step(
        self,
        session_id: str,
        observation: Observation,
        index: int,
        state: CameraRecoveryState,
        instruction: str,
        evidence: list[CameraEvidenceKind],
        permission: CameraPermissionState = CameraPermissionState.UNKNOWN,
        *,
        profile: str = FIXTURE_PROFILE,
    ) -> CameraDecision:
        return CameraDecision(
            GuidanceResult(
                status="next_step",
                instruction=instruction,
                target=self._target(session_id, observation, index),
            ),
            self._recovery(state, evidence, permission, profile=profile),
        )

    def guide(
        self,
        session_id: str,
        observation: Observation,
        verification: CameraReadyVerification | None,
        profile_name: str = FIXTURE_PROFILE,
    ) -> CameraDecision:
        record = self._record(session_id)
        self._accept_observation(record, observation)
        record.last_target_id = None
        record.last_target_digest = None
        if profile_name == LIVE_PROFILE:
            decision = self._guide_live(session_id, observation, verification)
        elif profile_name != FIXTURE_PROFILE:
            raise CameraRecoveryError("Unsupported camera recovery profile")
        elif observation.application in SUPPORTED_PROFILE.teams_applications:
            decision = self._guide_teams(session_id, record, observation, verification)
        elif observation.application in SUPPORTED_PROFILE.settings_applications:
            decision = self._guide_settings(session_id, record, observation, verification)
        else:
            if verification is not None:
                raise CameraRecoveryError("Camera readiness cannot be verified on an unsupported surface")
            decision = self._clarification(
                CameraRecoveryState.UNSUPPORTED,
                "This fixture supports only the configured Teams pre-join and Windows Settings surfaces.",
                [],
            )
        record.last_state = decision.recovery.state
        return decision

    def _guide_live(
        self,
        session_id: str,
        observation: Observation,
        verification: CameraReadyVerification | None,
    ) -> CameraDecision:
        if verification is not None:
            raise CameraRecoveryError(
                "The live-probe profile cannot verify permission restoration or camera readiness"
            )
        if observation.application in SUPPORTED_PROFILE.teams_applications:
            return self._guide_live_teams(session_id, observation)
        if observation.application in SUPPORTED_PROFILE.settings_applications:
            evidence = [CameraEvidenceKind.SYSTEM_CAMERA_SETTINGS_SURFACE]
            if not observation.elements:
                evidence.append(CameraEvidenceKind.UIA_NO_DESCENDANTS)
            return self._clarification(
                CameraRecoveryState.SYSTEM_CAMERA_SETTINGS_UNINSPECTABLE,
                "Windows Camera Settings UIA targeting is not probe-backed on this build; use an explicitly approved private visual verifier or a fixture, not a UIA target.",
                evidence,
                profile=LIVE_PROFILE,
            )
        return self._clarification(
            CameraRecoveryState.UNSUPPORTED,
            "The live-probe profile supports only the selected Teams HWND tree and fail-closed Windows Settings detection.",
            [],
            profile=LIVE_PROFILE,
        )

    def _guide_live_teams(self, session_id: str, observation: Observation) -> CameraDecision:
        evidence = [CameraEvidenceKind.TEAMS_SELECTED_WINDOW]
        if _has_match(observation, LIVE_DEVICES_MARKER):
            evidence.append(CameraEvidenceKind.TEAMS_DEVICES_SURFACE)
            status, index = _candidate(observation, LIVE_OPEN_CAMERA_SETTINGS)
            if status == "ambiguous":
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "More than one equally ranked open-camera-settings control was observed.",
                    evidence,
                    profile=LIVE_PROFILE,
                )
            if status == "matched" and observation.elements[index].isEnabled is True:
                evidence.append(CameraEvidenceKind.OPEN_SYSTEM_CAMERA_SETTINGS)
                return self._next_step(
                    session_id,
                    observation,
                    index,
                    CameraRecoveryState.TEAMS_DEVICES_OPEN,
                    "Open Windows camera settings yourself. The backend will not assume its UIA tree is inspectable.",
                    evidence,
                    profile=LIVE_PROFILE,
                )
            return self._clarification(
                CameraRecoveryState.TEAMS_DEVICES_OPEN,
                "The probe-backed Teams Devices surface is visible, but no unique enabled open-camera-settings control is safe to target.",
                evidence,
                profile=LIVE_PROFILE,
            )
        for predicate, kind, state, instruction in (
            (
                LIVE_DEVICES_TAB,
                CameraEvidenceKind.TEAMS_DEVICES_TAB,
                CameraRecoveryState.TEAMS_SETTINGS_MENU_OPEN,
                "Open the Teams Devices tab yourself.",
            ),
            (
                LIVE_SETTINGS_ITEM,
                CameraEvidenceKind.TEAMS_SETTINGS_ITEM,
                CameraRecoveryState.TEAMS_SETTINGS_MENU_OPEN,
                "Open Teams Settings yourself.",
            ),
            (
                LIVE_MORE_OPTIONS,
                CameraEvidenceKind.TEAMS_MORE_OPTIONS,
                CameraRecoveryState.TEAMS_PREJOIN_OBSERVED,
                "Open Settings and more yourself.",
            ),
        ):
            status, index = _candidate(observation, predicate)
            if status == "ambiguous":
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "More than one equally ranked probe-backed Teams navigation control was observed.",
                    evidence,
                    profile=LIVE_PROFILE,
                )
            if status == "matched":
                evidence.append(kind)
                if observation.elements[index].isEnabled is True:
                    return self._next_step(
                        session_id,
                        observation,
                        index,
                        state,
                        instruction,
                        evidence,
                        profile=LIVE_PROFILE,
                    )
                return self._clarification(
                    state,
                    "The probe-backed Teams navigation control is not explicitly enabled.",
                    evidence,
                    profile=LIVE_PROFILE,
                )
        return self._clarification(
            CameraRecoveryState.TEAMS_PREJOIN_OBSERVED,
            "The selected Teams HWND tree was observed, but no unique probe-backed camera-settings navigation control is visible.",
            evidence,
            profile=LIVE_PROFILE,
        )

    def _guide_teams(
        self,
        session_id: str,
        record: RecoveryRecord,
        observation: Observation,
        verification: CameraReadyVerification | None,
    ) -> CameraDecision:
        profile = SUPPORTED_PROFILE
        camera_status, camera_index = _candidate(observation, profile.teams_camera)
        if camera_status == "ambiguous":
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "More than one equally ranked camera control was observed; capture the pre-join screen again.",
                [CameraEvidenceKind.TEAMS_PREJOIN_SURFACE],
            )
        if record.permission_on_observed:
            if camera_status == "missing":
                if verification is not None:
                    raise CameraRecoveryError("Camera readiness requires the configured Teams camera control")
                return self._clarification(
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "Return to the configured Teams pre-join screen and capture its camera control.",
                    [CameraEvidenceKind.RETURNED_TO_TEAMS],
                    CameraPermissionState.ON,
                )
            camera = observation.elements[camera_index]
            evidence = [CameraEvidenceKind.RETURNED_TO_TEAMS]
            if camera.isEnabled is not True or camera.toggleState == ToggleState.INDETERMINATE:
                if verification is not None:
                    raise CameraRecoveryError("Camera readiness conflicts with disabled or indeterminate UI evidence")
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "The Teams camera control is not deterministically enabled; verification cannot continue.",
                    evidence,
                    CameraPermissionState.ON,
                )
            if camera.toggleState == ToggleState.OFF:
                if verification is not None:
                    raise CameraRecoveryError("Camera readiness conflicts with an observed off camera control")
                evidence.append(CameraEvidenceKind.CAMERA_TOGGLE_OFF)
                return self._next_step(
                    session_id,
                    observation,
                    camera_index,
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "Turn on the Teams camera yourself, then capture the same pre-join state for local verification.",
                    evidence,
                    CameraPermissionState.ON,
                )
            if camera.toggleState != ToggleState.ON:
                if verification is not None:
                    raise CameraRecoveryError("Camera readiness requires an explicit on camera state")
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "The Teams camera control lacks an explicit on/off state.",
                    evidence,
                    CameraPermissionState.ON,
                )
            evidence.append(CameraEvidenceKind.CAMERA_TOGGLE_ON)
            if verification is None:
                return self._clarification(
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "Permission is restored and Teams shows camera on, but local camera readiness verification is still required.",
                    evidence,
                    CameraPermissionState.ON,
                )
            evidence.append(CameraEvidenceKind.LOCAL_CAMERA_VERIFIER)
            return CameraDecision(
                GuidanceResult(
                    status="completed",
                    instruction="Camera readiness is verified by fresh Teams UI evidence and the bound local camera verifier.",
                ),
                self._recovery(
                    CameraRecoveryState.CAMERA_READY_VERIFIED,
                    evidence,
                    CameraPermissionState.ON,
                    verified=True,
                ),
            )

        if verification is not None:
            raise CameraRecoveryError("Camera readiness was supplied before permission restoration was observed")
        evidence = [CameraEvidenceKind.TEAMS_PREJOIN_SURFACE]
        if camera_status == "missing":
            return self._clarification(
                CameraRecoveryState.TEAMS_PREJOIN_OBSERVED,
                "The configured Teams pre-join surface was observed without one reliable camera control.",
                evidence,
            )
        camera = observation.elements[camera_index]
        if camera.toggleState == ToggleState.ON:
            evidence.append(CameraEvidenceKind.CAMERA_TOGGLE_ON)
            return self._clarification(
                CameraRecoveryState.UNSUPPORTED,
                "The camera is already on, so this permission-recovery fixture cannot establish the reported cause.",
                evidence,
            )
        if camera.toggleState != ToggleState.OFF:
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "The Teams camera control lacks an explicit off state.",
                evidence,
            )
        evidence.append(CameraEvidenceKind.CAMERA_TOGGLE_OFF)
        blocked_status, _ = _candidate(observation, profile.teams_blocked)
        if blocked_status == "ambiguous":
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "More than one equally ranked camera-block indicator was observed.",
                evidence,
            )
        if blocked_status != "matched":
            return self._clarification(
                CameraRecoveryState.TEAMS_PREJOIN_OBSERVED,
                "Camera is off, but the configured permission-block evidence is absent; do not assume the cause.",
                evidence,
            )
        record.block_confirmed = True
        evidence.append(CameraEvidenceKind.CAMERA_BLOCK_INDICATOR)
        settings_status, settings_index = _candidate(observation, profile.teams_open_settings)
        if settings_status == "ambiguous":
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "More than one equally ranked camera-settings control was observed.",
                evidence,
            )
        if settings_status == "matched" and observation.elements[settings_index].isEnabled is True:
            return self._next_step(
                session_id,
                observation,
                settings_index,
                CameraRecoveryState.CAMERA_BLOCK_CONFIRMED,
                "Open camera settings yourself using the highlighted fixture control.",
                evidence,
            )
        return self._clarification(
            CameraRecoveryState.CAMERA_BLOCK_CONFIRMED,
            "The permission block is confirmed, but no enabled fixture control can open camera settings.",
            evidence,
        )

    def _guide_settings(
        self,
        session_id: str,
        record: RecoveryRecord,
        observation: Observation,
        verification: CameraReadyVerification | None,
    ) -> CameraDecision:
        if verification is not None:
            raise CameraRecoveryError("Camera readiness verification is accepted only with matching Teams evidence")
        evidence = [CameraEvidenceKind.CAMERA_SETTINGS_SURFACE]
        if not record.block_confirmed:
            return self._clarification(
                CameraRecoveryState.UNSUPPORTED,
                "Camera settings are visible, but this session did not first confirm the configured Teams permission block.",
                evidence,
            )
        status, index = _candidate(observation, SUPPORTED_PROFILE.settings_permission)
        if status == "ambiguous":
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "More than one equally ranked applicable camera-permission toggle was observed.",
                evidence,
            )
        if status == "missing":
            return self._clarification(
                CameraRecoveryState.CAMERA_SETTINGS_OPEN,
                "Camera settings are open, but the fixture-specific applicable permission toggle is not visible.",
                evidence,
            )
        toggle = observation.elements[index]
        if toggle.isEnabled is False:
            return self._clarification(
                CameraRecoveryState.ADMIN_MANAGED,
                "The applicable camera permission is disabled or managed; MSGuide will not suggest changing it.",
                evidence,
                CameraPermissionState.MANAGED,
            )
        if toggle.toggleState == ToggleState.OFF:
            evidence.append(CameraEvidenceKind.APPLICABLE_PERMISSION_OFF)
            if toggle.isEnabled is not True:
                return self._clarification(
                    CameraRecoveryState.APPLICABLE_PERMISSION_OFF,
                    "The applicable permission is off, but enabled-state evidence is missing; no target is safe.",
                    evidence,
                    CameraPermissionState.OFF,
                )
            record.permission_on_observed = False
            record.permission_off_observed = True
            evidence.append(CameraEvidenceKind.PERMISSION_TOGGLE_ENABLED)
            return self._next_step(
                session_id,
                observation,
                index,
                CameraRecoveryState.USER_ACTION_REQUIRED,
                "Turn on the highlighted camera permission yourself, then capture settings again.",
                evidence,
                CameraPermissionState.OFF,
            )
        if toggle.toggleState == ToggleState.ON:
            evidence.append(CameraEvidenceKind.APPLICABLE_PERMISSION_ON)
            if not record.permission_off_observed:
                return self._clarification(
                    CameraRecoveryState.UNSUPPORTED,
                    "The permission is already on without a prior off observation in this session; do not declare recovery.",
                    evidence,
                    CameraPermissionState.ON,
                )
            record.permission_on_observed = True
            return self._clarification(
                CameraRecoveryState.APPLICABLE_PERMISSION_ON,
                "Permission restoration is observed. Return to Teams; camera readiness is not complete until locally verified.",
                evidence,
                CameraPermissionState.ON,
            )
        return self._clarification(
            CameraRecoveryState.AMBIGUOUS,
            "The applicable permission lacks an explicit on/off state.",
            evidence,
        )

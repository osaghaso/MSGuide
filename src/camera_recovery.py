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
    TeamsCameraSessionState,
    ToggleState,
    UIElement,
)


FIXTURE_PROFILE = "teams-camera-recovery-win11-24h2-en-US-fixture-v1"
LIVE_PROFILE = "teams-camera-recovery-new-teams-uia-probe-20260916-v1"
PINNED_PROFILE = "teams-camera-recovery-pinned-20260916-v2"


class CameraRecoveryError(ValueError):
    """The supplied evidence cannot safely advance the deterministic scenario."""


@dataclass(frozen=True)
class ElementPredicate:
    automation_ids: tuple[str, ...]
    labels: tuple[str, ...]
    roles: tuple[str, ...]
    frameworks: tuple[str, ...]
    label_fallback_with_automation: bool = False
    id_without_framework: bool = False


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
LIVE_CAMERA_SELECTOR = ElementPredicate(
    automation_ids=("camera", "camera-selector"),
    labels=("Camera",),
    roles=("combobox",),
    frameworks=("chrome", "webview2"),
    label_fallback_with_automation=True,
)
PINNED_SYSTEM_GLOBAL = ElementPredicate(
    automation_ids=("systemsettings_capabilityaccess_camera_systemglobal_toggleswitch",),
    labels=(),
    roles=("checkbox", "switch", "togglebutton"),
    frameworks=("xaml",),
    id_without_framework=True,
)
PINNED_USER_GLOBAL = ElementPredicate(
    automation_ids=("systemsettings_capabilityaccess_camera_userglobal_toggleswitch",),
    labels=(),
    roles=("checkbox", "switch", "togglebutton"),
    frameworks=("xaml",),
    id_without_framework=True,
)
PINNED_TEAMS_PERMISSION = ElementPredicate(
    automation_ids=("msteams_8wekyb3d8bbwe_toggleswitch",),
    labels=("Microsoft Teams Currently in use",),
    roles=("checkbox", "switch", "togglebutton"),
    frameworks=("xaml",),
    id_without_framework=True,
)


@dataclass
class RecoveryRecord:
    profile_name: str | None = None
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
            or _normalized(element.role) not in predicate.roles):
        return 0
    if element.automationId is not None:
        if _normalized(element.automationId) in predicate.automation_ids:
            if (predicate.id_without_framework
                    or _normalized(element.frameworkId) in predicate.frameworks):
                return 100
            return 0
        if not predicate.label_fallback_with_automation:
            return 0
    if (element.label in predicate.labels
            and _normalized(element.frameworkId) in predicate.frameworks):
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
            isOffscreen=element.isOffscreen,
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
                and element.isOffscreen == target.isOffscreen
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
        settings_launch_uri: str | None = None,
    ) -> CameraRecoveryResponse:
        payload = dict(
            profile=profile,
            evidenceBasis=(CameraEvidenceBasis.FIXTURE
                           if profile == FIXTURE_PROFILE else CameraEvidenceBasis.LIVE_PROBE),
            fixtureSupported=profile == FIXTURE_PROFILE,
            settingsUiaProven=profile == PINNED_PROFILE,
            rawPixelEvidenceUsed=False,
            state=state,
            evidence=evidence,
            permissionState=permission,
            verificationRequired=not verified,
        )
        if settings_launch_uri is not None:
            payload["settingsLaunchUri"] = settings_launch_uri
        return CameraRecoveryResponse(**payload)

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

    def _untargeted_next_step(
        self,
        state: CameraRecoveryState,
        instruction: str,
        evidence: list[CameraEvidenceKind],
        *,
        profile: str,
        settings_launch_uri: str,
    ) -> CameraDecision:
        return CameraDecision(
            GuidanceResult(status="next_step", instruction=instruction),
            self._recovery(
                state,
                evidence,
                profile=profile,
                settings_launch_uri=settings_launch_uri,
            ),
        )

    def guide(
        self,
        session_id: str,
        observation: Observation,
        verification: CameraReadyVerification | None,
        profile_name: str = FIXTURE_PROFILE,
    ) -> CameraDecision:
        record = self._record(session_id)
        if record.profile_name != profile_name:
            record = RecoveryRecord(profile_name=profile_name)
            self._records[session_id] = record
        self._accept_observation(record, observation)
        record.last_target_id = None
        record.last_target_digest = None
        if profile_name in {LIVE_PROFILE, PINNED_PROFILE}:
            decision = self._guide_live(
                session_id, record, observation, verification, profile_name
            )
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
        record: RecoveryRecord,
        observation: Observation,
        verification: CameraReadyVerification | None,
        profile_name: str,
    ) -> CameraDecision:
        if profile_name == LIVE_PROFILE and verification is not None:
            raise CameraRecoveryError(
                "The live-probe profile cannot verify permission restoration or camera readiness"
            )
        if observation.application in SUPPORTED_PROFILE.teams_applications:
            return self._guide_live_teams(
                session_id, record, observation, verification, profile_name
            )
        if observation.application in SUPPORTED_PROFILE.settings_applications:
            if verification is not None:
                raise CameraRecoveryError(
                    "Camera readiness verification is accepted only with matching Teams evidence"
                )
            if profile_name == PINNED_PROFILE:
                return self._guide_pinned_settings(session_id, record, observation)
            evidence = [CameraEvidenceKind.SYSTEM_CAMERA_SETTINGS_SURFACE]
            if not observation.elements:
                evidence.append(CameraEvidenceKind.UIA_NO_DESCENDANTS)
            return self._clarification(
                CameraRecoveryState.SYSTEM_CAMERA_SETTINGS_UNINSPECTABLE,
                "Windows Camera Settings UIA targeting is not probe-backed on this build; use an explicitly approved private visual verifier or a fixture, not a UIA target.",
                evidence,
                profile=profile_name,
            )
        return self._clarification(
            CameraRecoveryState.UNSUPPORTED,
            "The live-probe profile supports only the selected Teams HWND tree and fail-closed Windows Settings detection.",
            [],
            profile=profile_name,
        )

    def _guide_live_teams(
        self,
        session_id: str,
        record: RecoveryRecord,
        observation: Observation,
        verification: CameraReadyVerification | None,
        profile_name: str,
    ) -> CameraDecision:
        evidence = [CameraEvidenceKind.TEAMS_SELECTED_WINDOW]
        if verification is not None and not record.permission_on_observed:
            raise CameraRecoveryError(
                "Camera readiness was supplied before the pinned permission restoration was observed"
            )
        if record.permission_on_observed:
            reinitialized = (
                observation.teamsCameraSessionState == TeamsCameraSessionState.REINITIALIZED
            )
            if verification is not None and verification.reinitializationMethod is None:
                raise CameraRecoveryError(
                    "Pinned camera readiness requires an explicit Teams camera reinitialization method"
                )
            if not reinitialized:
                if verification is not None:
                    raise CameraRecoveryError(
                        "Camera readiness requires explicit reinitialized session evidence"
                    )
                return self._clarification(
                    CameraRecoveryState.CAMERA_REINITIALIZATION_REQUIRED,
                    "Reopen pre-join, relaunch Teams, or explicitly reinitialize the camera before readiness verification.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            evidence.append(CameraEvidenceKind.CAMERA_REINITIALIZED)
            if not _has_match(observation, LIVE_DEVICES_MARKER):
                if verification is not None:
                    raise CameraRecoveryError(
                        "Camera readiness requires the probe-backed Teams Devices surface"
                    )
                return self._clarification(
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "Return to the probe-backed Teams Devices page for local camera verification.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            evidence.append(CameraEvidenceKind.TEAMS_DEVICES_SURFACE)
            status, index = _candidate(observation, LIVE_CAMERA_SELECTOR)
            if status == "ambiguous":
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "More than one equally ranked Teams camera selector was observed.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            if status != "matched":
                if verification is not None:
                    raise CameraRecoveryError(
                        "Camera readiness requires the probe-backed Teams camera selector"
                    )
                return self._clarification(
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "The Teams Devices page is visible, but its camera selector is not reliably identified.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            selector = observation.elements[index]
            if selector.isEnabled is not True or selector.isOffscreen is True:
                if verification is not None:
                    raise CameraRecoveryError(
                        "Camera readiness conflicts with disabled or offscreen selector evidence"
                    )
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "The Teams camera selector is not explicitly enabled and onscreen.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            evidence.append(CameraEvidenceKind.TEAMS_CAMERA_SELECTOR)
            if verification is None:
                return self._clarification(
                    CameraRecoveryState.RETURN_TO_TEAMS,
                    "Permission is restored and the Teams camera selector is visible, but fresh local preview verification is still required.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=profile_name,
                )
            evidence.append(CameraEvidenceKind.LOCAL_CAMERA_VERIFIER)
            return CameraDecision(
                GuidanceResult(
                    status="completed",
                    instruction="Camera readiness is verified by pinned permission evidence, Teams Devices UIA, and the bound local preview verifier.",
                ),
                self._recovery(
                    CameraRecoveryState.CAMERA_READY_VERIFIED,
                    evidence,
                    CameraPermissionState.ON,
                    verified=True,
                    profile=profile_name,
                ),
            )
        if _has_match(observation, LIVE_DEVICES_MARKER):
            evidence.append(CameraEvidenceKind.TEAMS_DEVICES_SURFACE)
            if profile_name == PINNED_PROFILE:
                evidence.append(CameraEvidenceKind.PINNED_CAMERA_SETTINGS_URI)
                return self._untargeted_next_step(
                    CameraRecoveryState.TEAMS_DEVICES_OPEN,
                    "Open the pinned Camera privacy URI, then verify the exact Camera page IDs; do not assume the URI landing.",
                    evidence,
                    profile=profile_name,
                    settings_launch_uri="ms-settings:privacy-webcam",
                )
            status, index = _candidate(observation, LIVE_OPEN_CAMERA_SETTINGS)
            if status == "ambiguous":
                return self._clarification(
                    CameraRecoveryState.AMBIGUOUS,
                    "More than one equally ranked open-camera-settings control was observed.",
                    evidence,
                    profile=profile_name,
                )
            if status == "matched" and observation.elements[index].isEnabled is True:
                evidence.append(CameraEvidenceKind.OPEN_SYSTEM_CAMERA_SETTINGS)
                instruction = (
                    "Open Windows camera settings yourself, then verify the exact pinned Camera page IDs."
                    if profile_name == PINNED_PROFILE
                    else "Open Windows camera settings yourself. The backend will not assume its UIA tree is inspectable."
                )
                return self._next_step(
                    session_id,
                    observation,
                    index,
                    CameraRecoveryState.TEAMS_DEVICES_OPEN,
                    instruction,
                    evidence,
                    profile=profile_name,
                )
            return self._clarification(
                CameraRecoveryState.TEAMS_DEVICES_OPEN,
                "The probe-backed Teams Devices surface is visible, but no unique enabled open-camera-settings control is safe to target.",
                evidence,
                profile=profile_name,
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
                    profile=profile_name,
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
                        profile=profile_name,
                    )
                return self._clarification(
                    state,
                    "The probe-backed Teams navigation control is not explicitly enabled.",
                    evidence,
                    profile=profile_name,
                )
        return self._clarification(
            CameraRecoveryState.TEAMS_PREJOIN_OBSERVED,
            "The selected Teams HWND tree was observed, but no unique probe-backed camera-settings navigation control is visible.",
            evidence,
            profile=profile_name,
        )

    def _guide_pinned_settings(
        self,
        session_id: str,
        record: RecoveryRecord,
        observation: Observation,
    ) -> CameraDecision:
        evidence = [CameraEvidenceKind.SYSTEM_CAMERA_SETTINGS_SURFACE]
        candidates = [
            _candidate(observation, PINNED_SYSTEM_GLOBAL),
            _candidate(observation, PINNED_USER_GLOBAL),
            _candidate(observation, PINNED_TEAMS_PERMISSION),
        ]
        if any(status == "ambiguous" for status, _ in candidates):
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "The pinned Camera Settings page contains ambiguous required toggle evidence.",
                evidence,
                profile=PINNED_PROFILE,
            )
        if any(status != "matched" for status, _ in candidates):
            state = (CameraRecoveryState.SYSTEM_CAMERA_SETTINGS_UNINSPECTABLE
                     if not observation.elements
                     else CameraRecoveryState.SYSTEM_CAMERA_SETTINGS_UNVERIFIED)
            if not observation.elements:
                evidence.append(CameraEvidenceKind.UIA_NO_DESCENDANTS)
            return self._clarification(
                state,
                "Verify the Camera Settings page from its exact pinned toggle IDs; the deep link landing is not trusted.",
                evidence,
                profile=PINNED_PROFILE,
            )
        system = observation.elements[candidates[0][1]]
        user = observation.elements[candidates[1][1]]
        teams_index = candidates[2][1]
        teams = observation.elements[teams_index]
        if teams.label != "Microsoft Teams Currently in use":
            return self._clarification(
                CameraRecoveryState.SYSTEM_CAMERA_SETTINGS_UNVERIFIED,
                "The pinned packaged Teams toggle ID is present without its exact observed Microsoft Teams name.",
                evidence,
                profile=PINNED_PROFILE,
            )
        evidence.append(CameraEvidenceKind.CAMERA_SETTINGS_PAGE_VERIFIED)
        if system.isEnabled is False or user.isEnabled is False:
            return self._clarification(
                CameraRecoveryState.ADMIN_MANAGED,
                "A required global camera permission is disabled or managed; do not target the Teams toggle.",
                evidence,
                CameraPermissionState.MANAGED,
                profile=PINNED_PROFILE,
            )
        if system.toggleState != ToggleState.ON or user.toggleState != ToggleState.ON:
            return self._clarification(
                CameraRecoveryState.UNSUPPORTED,
                "The pinned global camera permissions are not both on; the individual Teams toggle is not the applicable fix.",
                evidence,
                profile=PINNED_PROFILE,
            )
        evidence.extend([
            CameraEvidenceKind.SYSTEM_CAMERA_GLOBAL_ON,
            CameraEvidenceKind.USER_CAMERA_GLOBAL_ON,
            CameraEvidenceKind.TEAMS_CAMERA_PERMISSION,
        ])
        if teams.isEnabled is False:
            return self._clarification(
                CameraRecoveryState.ADMIN_MANAGED,
                "The packaged Teams camera permission is disabled or managed; MSGuide will not target it.",
                evidence,
                CameraPermissionState.MANAGED,
                profile=PINNED_PROFILE,
            )
        if teams.isEnabled is not True or teams.isOffscreen is not False:
            return self._clarification(
                CameraRecoveryState.AMBIGUOUS,
                "The packaged Teams permission lacks explicit enabled and visible evidence.",
                evidence,
                profile=PINNED_PROFILE,
            )
        if teams.toggleState == ToggleState.OFF:
            if observation.teamsCameraSessionState != TeamsCameraSessionState.NOT_INITIALIZED:
                state = (CameraRecoveryState.UNSUPPORTED
                         if observation.teamsCameraSessionState == TeamsCameraSessionState.ACTIVE
                         else CameraRecoveryState.AMBIGUOUS)
                return self._clarification(
                    state,
                    "Prepare the packaged permission off before Teams initializes a camera session; changing an active session is not recovery evidence.",
                    evidence,
                    CameraPermissionState.OFF,
                    profile=PINNED_PROFILE,
                )
            record.permission_on_observed = False
            record.permission_off_observed = True
            evidence.extend([
                CameraEvidenceKind.PERMISSION_PREPARED_BEFORE_CAMERA,
                CameraEvidenceKind.APPLICABLE_PERMISSION_OFF,
            ])
            return self._next_step(
                session_id,
                observation,
                teams_index,
                CameraRecoveryState.USER_ACTION_REQUIRED,
                "Turn on the highlighted packaged Microsoft Teams camera permission yourself, then capture the verified page again.",
                evidence,
                CameraPermissionState.OFF,
                profile=PINNED_PROFILE,
            )
        if teams.toggleState == ToggleState.ON:
            evidence.append(CameraEvidenceKind.APPLICABLE_PERMISSION_ON)
            if not record.permission_off_observed:
                return self._clarification(
                    CameraRecoveryState.UNSUPPORTED,
                    "The packaged Teams permission is already on without a prior off observation in this pinned session.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=PINNED_PROFILE,
                )
            if observation.teamsCameraSessionState != TeamsCameraSessionState.NOT_INITIALIZED:
                return self._clarification(
                    CameraRecoveryState.UNSUPPORTED,
                    "Teams initialized its camera before the prepared permission was restored; restart the pinned sequence.",
                    evidence,
                    CameraPermissionState.ON,
                    profile=PINNED_PROFILE,
                )
            record.permission_on_observed = True
            return self._clarification(
                CameraRecoveryState.APPLICABLE_PERMISSION_ON,
                "The packaged Teams permission changed from off to on. Return to Teams Devices for local preview verification.",
                evidence,
                CameraPermissionState.ON,
                profile=PINNED_PROFILE,
            )
        return self._clarification(
            CameraRecoveryState.AMBIGUOUS,
            "The packaged Teams permission lacks an explicit on/off state.",
            evidence,
            profile=PINNED_PROFILE,
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

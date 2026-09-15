"""Local-demo policy: no employee authentication or enterprise source ACLs."""

from dataclasses import dataclass
from enum import Enum

from src.models import ActionRiskLevel, SensitivityLevel


@dataclass(frozen=True)
class EmployeeIdentity:
    """Legacy name; a local principal, not a verified employee."""
    objectId: str = "local"
    email: str = ""
    tenant: str = ""
    deviceId: str = ""
    hasCompliance: bool = False


class PolicyDecision(str, Enum):
    ALLOW = "allow"
    ALLOW_WITH_CONFIRMATION = "allow_with_confirmation"
    DENY = "deny"
    STEP_UP_AUTH = "step_up_auth"


class PolicyEngine:
    def evaluate_context_sensitivity(self, context, identity):
        return PolicyDecision.ALLOW if context.sensitivity == SensitivityLevel.PUBLIC else PolicyDecision.DENY

    def evaluate_retrieval(self, identity, requestType, sourceUri):
        return PolicyDecision.ALLOW if sourceUri == "sample-docs" else PolicyDecision.DENY

    def classify_action_risk(self, tool: str, parameters: dict) -> ActionRiskLevel:
        if tool in {"modify_service_config", "delete_personal_data"}:
            return ActionRiskLevel.HIGH
        return {"view_logs": ActionRiskLevel.LOW, "create_work_item": ActionRiskLevel.MEDIUM}.get(tool, ActionRiskLevel.BLOCKED)

    def evaluate_action(self, preview, identity):
        risk = self.classify_action_risk(preview.tool, preview.parameters)
        if risk == ActionRiskLevel.HIGH:
            return PolicyDecision.STEP_UP_AUTH
        if risk == ActionRiskLevel.BLOCKED:
            return PolicyDecision.DENY
        return PolicyDecision.ALLOW_WITH_CONFIRMATION

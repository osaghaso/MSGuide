"""Small regression checks for deterministic local policy and sample retrieval."""

import asyncio
from types import SimpleNamespace

import pytest

from src.main import _classify_intent
from src.models import ActionRiskLevel, RequestType, SensitivityLevel
from src.policy import EmployeeIdentity, PolicyDecision, PolicyEngine
from src.retrieval import RetrieverMock


@pytest.mark.parametrize("prompt,expected", [("How do I deploy?", RequestType.GUIDE),
    ("How do I create a work item?", RequestType.GUIDE), ("Create a work item", RequestType.ACTION),
    ("documentation about runtime", RequestType.EXPLAIN), ("Find build errors", RequestType.RETRIEVE)])
def test_word_boundary_intent(prompt, expected):
    assert _classify_intent(prompt) == expected


def test_policy_allowlist_and_risk_not_trusted():
    policy = PolicyEngine()
    identity = EmployeeIdentity()
    assert policy.classify_action_risk("unknown", {}) == ActionRiskLevel.BLOCKED
    for tool, decision in [("delete_personal_data", PolicyDecision.STEP_UP_AUTH),
                           ("deploy_production", PolicyDecision.DENY),
                           ("view_logs", PolicyDecision.ALLOW_WITH_CONFIRMATION)]:
        fake_preview = SimpleNamespace(tool=tool, parameters={}, riskLevel=ActionRiskLevel.LOW)
        assert policy.evaluate_action(fake_preview, identity) == decision
    for sensitivity in SensitivityLevel:
        assert policy.evaluate_context_sensitivity(SimpleNamespace(sensitivity=sensitivity), identity) == (
            PolicyDecision.ALLOW if sensitivity == SensitivityLevel.PUBLIC else PolicyDecision.DENY)


def test_retrieval_is_public_sample_not_email_auth(caplog):
    retriever = RetrieverMock()
    async def check():
        results = await retriever.retrieve("How do I deploy?", "unverified@external.invalid")
        assert len(results) == 1
        assert results[0].classification == SensitivityLevel.PUBLIC
        assert results[0].sourceUri.startswith("https://example.invalid/")
        assert "sample" in results[0].authorizationEvidence.lower()
        assert await retriever.retrieve("xyzabc123notfound", "") == []
        assert await retriever.retrieve("build private-query-marker", "")
    asyncio.run(check())
    assert "private-query-marker" not in caplog.text

"""Bundled public sample passages, not live search or enterprise authorization."""

import re
from datetime import datetime, timezone

from src.models import RetrievedPassage, SensitivityLevel


class RetrieverMock:
    async def retrieve(self, query: str, user_email: str = "") -> list[RetrievedPassage]:
        words = set(re.findall(r"\b\w+\b", query.casefold()))
        samples = (
            ({"deploy", "deployment", "deployments", "staging"}, "deployment",
             "Sample Deployment Guide", "Sample only: deploy to a test environment, run smoke tests, and review the result before considering production."),
            ({"build", "compiler", "error", "errors", "troubleshooting", "dependencies"}, "build",
             "Sample Build Troubleshooting", "Sample only: for build exit code 1, inspect compiler errors and missing dependencies in the build log."),
        )
        return [RetrievedPassage(
            content=content, sourceUri=f"https://example.invalid/msguide/samples/{slug}",
            title=title, modifiedTime=datetime(2026, 9, 1, tzinfo=timezone.utc),
            classification=SensitivityLevel.PUBLIC,
            authorizationEvidence="Bundled synthetic public sample; no ACL check",
        ) for terms, slug, title, content in samples if words & terms]

"""Bounded in-memory simulation. Never invokes a shell, desktop, or external service."""

import asyncio
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import Enum


class ActionStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    SUCCESS = "success"
    FAILED = "failed"
    CANCELLED = "cancelled"


@dataclass
class ActionResult:
    jobId: str
    owner: str
    expires_at: datetime
    status: ActionStatus = ActionStatus.PENDING
    result: dict | None = None
    error: str | None = None
    task: asyncio.Task | None = None

    def response(self):
        return {"jobId": self.jobId, "status": self.status.value, "result": self.result,
                "error": self.error, "mock": True,
                "progress": 100 if self.status == ActionStatus.SUCCESS else 0}


class ActionRunner:
    def __init__(self):
        self.jobs: dict[str, ActionResult] = {}

    def start(self, job_id: str, owner: str, tool: str, parameters: dict):
        if tool not in {"view_logs", "create_work_item"}:
            raise ValueError("Unknown mock tool")
        job = ActionResult(job_id, owner, datetime.now(timezone.utc) + timedelta(minutes=10))
        self.jobs[job_id] = job
        job.task = asyncio.create_task(self._run(job, tool))
        return job

    async def _run(self, job, tool):
        try:
            job.status = ActionStatus.RUNNING
            # ponytail: a bounded cancellable simulation, not a connector or durable queue.
            await asyncio.sleep(0.25)
            job.result = {"mock": True, "tool": tool, "externalEffects": False,
                          "message": "Simulation completed; no external action was performed."}
            job.status = ActionStatus.SUCCESS
        except asyncio.CancelledError:
            job.status = ActionStatus.CANCELLED

    def cancel(self, job):
        if job.status in {ActionStatus.PENDING, ActionStatus.RUNNING}:
            job.status = ActionStatus.CANCELLED
            if job.task:
                job.task.cancel()
        return job

    async def close(self):
        tasks = [job.task for job in self.jobs.values() if job.task is not None]
        for job in self.jobs.values():
            self.cancel(job)
        await asyncio.gather(*tasks, return_exceptions=True)

"""Job deadlines and cancellation shared by Android debug transports."""
from contextvars import ContextVar
from threading import Event
import time

from .runtime import AndroidError


current_job = ContextVar("android_job", default=None)


class JobStopped(AndroidError):
    pass


class Budget:
    def __init__(self, timeout):
        self.deadline = time.monotonic() + timeout
        self.cancelled = Event()
        self.stage = "queued"

    def check(self, stage=None):
        if stage:
            self.stage = stage
        if self.cancelled.is_set():
            raise JobStopped("Android operation cancelled")
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise JobStopped("Android operation exceeded its deadline")
        return remaining


def checkpoint(stage=None):
    job = current_job.get()
    if job:
        return job.check(stage)
    return float("inf")

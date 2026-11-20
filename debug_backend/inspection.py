"""Capability discovery and dispatch for platform-specific debugger views."""
from dataclasses import dataclass
from typing import Callable

from .errors import DebugEngineError


@dataclass(frozen=True)
class InspectionView:
    engine: str
    tools: dict[str, str]
    inspect: Callable


class DebugPlatform:
    def __init__(self, platform, target_description, views, domains, *, resolve_view=None):
        self.platform = platform
        self.target_description = target_description
        self.views = views
        self.domains = domains
        self.resolve_view = resolve_view

    def capabilities(self, domain=""):
        if domain and domain not in self.domains:
            raise DebugEngineError("UNSUPPORTED_DOMAIN", f"Choose a domain from {', '.join(self.domains)}")
        result = {
            "platform": self.platform,
            "target": self.target_description,
            "domains": list(self.domains),
            "inspectionViews": {
                name: {
                    "engine": view.engine,
                    "queries": list(view.tools),
                    **({"directToolsByQuery": dict(view.tools)} if domain == "inspection" else {}),
                }
                for name, view in self.views.items()
            },
        }
        if domain:
            result["domain"] = domain
            result["directTools"] = list(self.domains[domain])
        return result

    def inspect(self, target, query, view):
        if self.resolve_view and (not view or view in self.views):
            view = self.resolve_view(target, view)
        selected = self.views.get(view)
        if not selected or query not in selected.tools:
            raise DebugEngineError("UNSUPPORTED_INSPECTION",
                                   "Use debug_capabilities for the supported views and queries")
        return selected.inspect(target, query)

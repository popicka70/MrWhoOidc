"""
Evaluates page screenshots using a vision LLM.

Supports two backends selected via LLM_BACKEND env var:
  - "openai"  (default) – OpenAI GPT-4o Vision; requires OPENAI_API_KEY
  - "ollama"             – local Ollama instance via native /api/chat endpoint;
                          set OLLAMA_MODEL (default gemma3:27b-cloud) and
                          OLLAMA_HOST (default http://localhost:11434).
                          Cloud-proxied models (names ending in :cloud) skip
                          vision and go straight to text-only evaluation.

Usage:
    evaluator = LLMEvaluator()
    if evaluator.available:
        result = evaluator.evaluate(screenshot_path, route, instructions_content=...)
        print(result.summary)
"""

from __future__ import annotations

import base64
import json
import logging
import os
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

OLLAMA_TIMEOUT = 60  # seconds; generous for cloud-routed models

logger = logging.getLogger(__name__)

_SYSTEM_PROMPT = """You are a senior UX/UI quality reviewer evaluating a screenshot of a web application page.
Your task is to assess the visual quality, usability, and functional correctness of the page shown.

You MUST respond with a JSON object matching this exact schema (no markdown fences, raw JSON only):
{
  "page_name": "<inferred name of the page>",
  "overall_score": <integer 1-10>,
  "category_scores": {
    "layout_alignment": <integer 1-10>,
    "color_contrast": <integer 1-10>,
    "typography": <integer 1-10>,
    "visual_consistency": <integer 1-10>,
    "information_density": <integer 1-10>,
    "accessibility_hints": <integer 1-10>,
    "functional_state": <integer 1-10>
  },
  "issues": [
    {"severity": "high|medium|low", "description": "<issue description>"}
  ],
  "recommendations": ["<actionable improvement>"],
  "summary": "<2-4 sentence plain-language summary of the page's UI quality>"
}

Scoring guide (overall_score):
  10 – Exceptional: polished, consistent, fully accessible
   8 – Good: minor issues only
   6 – Adequate: noticeable but non-blocking issues
   4 – Poor: significant visual or usability problems
   2 – Critical: major layout breakage or accessibility failures
   1 – Unusable / blank / error page
"""

_USER_PROMPT_TEMPLATE = """Evaluate the following page screenshot.

Route: {route}

{instructions_section}

Evaluate all visible elements: navigation, form fields, tables, buttons, typography, color palette,
spacing/alignment, error messages, empty states, and overall polish. Respond with raw JSON only."""


@dataclass
class EvaluationResult:
    route: str
    screenshot_path: Path
    page_name: str = ""
    overall_score: int = 0
    category_scores: dict[str, int] = field(default_factory=dict)
    issues: list[dict] = field(default_factory=list)
    recommendations: list[str] = field(default_factory=list)
    summary: str = ""
    error: str | None = None
    skipped: bool = False

    @property
    def high_issues(self) -> list[dict]:
        return [i for i in self.issues if i.get("severity") == "high"]


class LLMEvaluator:
    """Sends screenshots to a vision LLM and returns structured evaluation results.

    Backend selection (LLM_BACKEND env var):
      "openai"  – OpenAI GPT-4o; requires OPENAI_API_KEY
      "ollama"  – local Ollama via native /api/chat; OLLAMA_MODEL + OLLAMA_HOST
    """

    def __init__(
        self,
        api_key: str | None = None,
        model: str | None = None,
        max_retries: int = 1,
        backend: str | None = None,
    ) -> None:
        self.backend = (backend or os.getenv("LLM_BACKEND", "openai")).lower()

        if self.backend == "ollama":
            self.ollama_host = os.getenv("OLLAMA_HOST", "http://localhost:11434")
            self.model = model or os.getenv("OLLAMA_MODEL", "gemma3:27b-cloud")
            # Cloud-proxied models don't support vision via Ollama
            self._vision_supported = "-cloud" not in self.model
            self.api_key = ""
        else:
            self.backend = "openai"
            self.api_key = api_key or os.getenv("OPENAI_API_KEY", "")
            self.model = model or os.getenv("OPENAI_MODEL", "gpt-4o")
            self._vision_supported = True

        self.max_retries = max_retries
        self._openai_client = None

    @property
    def available(self) -> bool:
        if self.backend == "ollama":
            return True
        return bool(self.api_key)

    def evaluate(
        self,
        screenshot_path: Path,
        route: str,
        instructions_content: str | None = None,
    ) -> EvaluationResult:
        """Evaluate *screenshot_path* for *route*; returns EvaluationResult."""
        result = EvaluationResult(route=route, screenshot_path=screenshot_path)

        if not self.available:
            result.skipped = True
            result.summary = "LLM evaluation skipped: no API key configured."
            return result

        if not screenshot_path.exists():
            result.error = f"Screenshot not found: {screenshot_path}"
            return result

        instructions_section = ""
        if instructions_content:
            instructions_section = f"Page-specific test instructions:\n\n{instructions_content}\n"

        user_prompt = _USER_PROMPT_TEMPLATE.format(
            route=route,
            instructions_section=instructions_section,
        )

        # --- Attempt vision evaluation (skipped for cloud Ollama models) ---
        if self._vision_supported:
            image_b64 = base64.b64encode(screenshot_path.read_bytes()).decode()
            for attempt in range(self.max_retries + 1):
                try:
                    text = self._call_vision(image_b64, user_prompt)
                    self._parse_response(text, result)
                    return result
                except Exception as exc:  # noqa: BLE001
                    logger.warning("Vision attempt %d failed for %s: %s", attempt + 1, route, exc)

        # --- Text-only evaluation (always attempted on vision failure / cloud model) ---
        text_prompt = (
            user_prompt
            + "\n\n(Note: no screenshot attached — evaluate based on route and instructions. "
            "Make reasonable assumptions about typical OIDC IdP UI for this page.)"
        )
        try:
            text = self._call_text(text_prompt)
            self._parse_response(text, result)
            if not self._vision_supported:
                result.summary = "[text-only] " + result.summary
            else:
                result.summary = "[text-only fallback] " + result.summary
        except Exception as exc:  # noqa: BLE001
            logger.error("Text evaluation failed for %s: %s", route, exc)
            result.error = str(exc)
            result.summary = f"Evaluation failed: {exc}"
        return result

    # ------------------------------------------------------------------
    # Backend implementations
    # ------------------------------------------------------------------

    def _call_vision(self, image_b64: str, user_prompt: str) -> str:
        """Vision call — only reached for non-cloud Ollama or OpenAI backend."""
        if self.backend == "ollama":
            return self._ollama_chat(
                messages=[
                    {"role": "user", "content": user_prompt, "images": [image_b64]}
                ]
            )
        # OpenAI
        client = self._get_openai_client()
        response = client.chat.completions.create(
            model=self.model,
            messages=[
                {"role": "system", "content": _SYSTEM_PROMPT},
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "image_url",
                            "image_url": {"url": f"data:image/png;base64,{image_b64}", "detail": "high"},
                        },
                        {"type": "text", "text": user_prompt},
                    ],
                },
            ],
            max_tokens=1500,
            temperature=0.2,
        )
        return response.choices[0].message.content or ""

    def _call_text(self, user_prompt: str) -> str:
        """Text-only call — works for all backends including cloud Ollama."""
        if self.backend == "ollama":
            return self._ollama_chat(
                messages=[
                    {"role": "system", "content": _SYSTEM_PROMPT},
                    {"role": "user", "content": user_prompt},
                ]
            )
        # OpenAI
        client = self._get_openai_client()
        response = client.chat.completions.create(
            model=self.model,
            messages=[
                {"role": "system", "content": _SYSTEM_PROMPT},
                {"role": "user", "content": user_prompt},
            ],
            max_tokens=1500,
            temperature=0.2,
        )
        return response.choices[0].message.content or ""

    def _ollama_chat(self, messages: list[dict]) -> str:
        """Call Ollama's native /api/chat endpoint directly (no openai library)."""
        payload = json.dumps(
            {"model": self.model, "messages": messages, "stream": False}
        ).encode()
        req = urllib.request.Request(
            f"{self.ollama_host}/api/chat",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=OLLAMA_TIMEOUT) as resp:
            data = json.loads(resp.read())
        return data["message"]["content"]

    def _get_openai_client(self):
        if self._openai_client is None:
            import openai  # lazy import
            self._openai_client = openai.OpenAI(
                api_key=self.api_key,
                timeout=30.0,
            )
        return self._openai_client

    @staticmethod
    def _parse_response(text: str, result: EvaluationResult) -> None:
        # Strip accidental markdown fences
        text = text.strip()
        if text.startswith("```"):
            text = "\n".join(text.split("\n")[1:])
            text = text.rstrip("`").strip()

        data = json.loads(text)
        result.page_name = data.get("page_name", "")
        result.overall_score = int(data.get("overall_score", 0))
        result.category_scores = data.get("category_scores", {})
        result.issues = data.get("issues", [])
        result.recommendations = data.get("recommendations", [])
        result.summary = data.get("summary", "")

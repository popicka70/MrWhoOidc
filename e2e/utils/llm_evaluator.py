"""
Evaluates page screenshots using a vision LLM.

Supports two backends selected via LLM_BACKEND env var:
  - "openai"  (default) – OpenAI GPT-4o Vision; requires OPENAI_API_KEY
  - "ollama"             – local Ollama instance via native /api/chat endpoint;
                          set OLLAMA_MODEL (default qwen3.5:397b-cloud) and
                          OLLAMA_HOST (default http://localhost:11434).
                          Set OLLAMA_VISION=0 to force text-only evaluation
                          (e.g. for models that don't support image input).

Usage:
    evaluator = LLMEvaluator()
    if evaluator.available:
        result = evaluator.evaluate(screenshot_path, route, instructions_content=...)
        print(result.summary)
"""

from __future__ import annotations

import base64
import io
import json
import logging
import os
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path

OLLAMA_TIMEOUT = 120  # seconds; generous for local models with complex prompts

logger = logging.getLogger(__name__)

_SYSTEM_PROMPT = """You are a UX reviewer for an OIDC admin web application.
Respond ONLY with raw JSON (NO markdown, NO explanation, ONLY the JSON object):
{"page_name":"<name>","overall_score":<1-10>,"summary":"<one sentence>"}
Scoring: 10=excellent 8=good 6=ok 4=poor 2=broken. Be concise."""

_USER_PROMPT_TEMPLATE = """Rate this page: {route}
{instructions_section}Consider: navigation, forms, buttons, tables, layout, errors, empty states. Raw JSON only."""

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
            self.model = model or os.getenv("OLLAMA_MODEL", "qwen3.5:397b-cloud")
            # Vision is on by default; set OLLAMA_VISION=0 to disable for text-only models
            self._vision_supported = os.getenv("OLLAMA_VISION", "1").strip() not in ("0", "false", "no")
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
            image_b64 = self._load_image_b64(screenshot_path)
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
                    {"role": "system", "content": _SYSTEM_PROMPT},
                    {"role": "user", "content": user_prompt, "images": [image_b64]},
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
        """Call Ollama's native /api/chat endpoint directly (no openai library).

        Uses think=false (top-level field) to suppress chain-of-thought reasoning
        in qwen3.x thinking models, preventing VRAM exhaustion on evaluation prompts.
        """
        payload = json.dumps(
            {
                "model": self.model,
                "messages": messages,
                "think": False,
                "stream": False,
                "options": {
                    "num_predict": 200,
                    "num_ctx": 2048,
                    "temperature": 0.2,
                },
            }
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

    @staticmethod
    def _load_image_b64(path: Path, max_width: int = 1280) -> str:
        """Load image, downscale to max_width if larger, return base64 PNG string."""
        from PIL import Image
        with Image.open(path) as img:
            if img.width > max_width:
                ratio = max_width / img.width
                new_size = (max_width, int(img.height * ratio))
                img = img.resize(new_size, Image.LANCZOS)
            buf = io.BytesIO()
            img.save(buf, format="PNG", optimize=True)
            return base64.b64encode(buf.getvalue()).decode()

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
        # Strip thinking blocks emitted by reasoning models (e.g. qwen3, deepseek-r1)
        import re
        text = re.sub(r"<think>.*?</think>", "", text, flags=re.DOTALL).strip()

        # Strip accidental markdown fences
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

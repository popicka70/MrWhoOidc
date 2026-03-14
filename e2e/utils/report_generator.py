"""
Aggregates per-page EvaluationResults into JSON and HTML reports.

Usage:
    generator = ReportGenerator(run_id="20260314_120000")
    generator.add(evaluation_result)
    generator.finalize()  # writes JSON + HTML
"""

from __future__ import annotations

import json
from datetime import datetime
from pathlib import Path
from typing import TYPE_CHECKING

from jinja2 import Environment, FileSystemLoader

if TYPE_CHECKING:
    from utils.llm_evaluator import EvaluationResult


class ReportGenerator:
    """Collects EvaluationResult instances and writes a final report."""

    def __init__(self, run_id: str | None = None, output_dir: Path | None = None) -> None:
        if output_dir is None:
            output_dir = Path(__file__).parent.parent / "reports"
        if run_id is None:
            run_id = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.run_id = run_id
        self.run_dir = output_dir / run_id
        self.run_dir.mkdir(parents=True, exist_ok=True)
        self._results: list[EvaluationResult] = []

    def add(self, result: "EvaluationResult") -> None:
        self._results.append(result)

    def finalize(self) -> tuple[Path, Path]:
        """Write JSON + HTML reports; return (json_path, html_path)."""
        json_path = self._write_json()
        html_path = self._write_html()
        return json_path, html_path

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    def _write_json(self) -> Path:
        path = self.run_dir / "report.json"
        data = {
            "run_id": self.run_id,
            "generated_at": datetime.now().isoformat(),
            "summary": self._build_summary(),
            "pages": [self._result_to_dict(r) for r in self._results],
        }
        path.write_text(json.dumps(data, indent=2, default=str), encoding="utf-8")
        return path

    def _write_html(self) -> Path:
        templates_dir = Path(__file__).parent.parent / "templates"
        env = Environment(
            loader=FileSystemLoader(str(templates_dir)),
            autoescape=True,
        )
        template = env.get_template("report.html.j2")
        context = {
            "run_id": self.run_id,
            "generated_at": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
            "summary": self._build_summary(),
            "results": self._results,
        }
        html_content = template.render(**context)
        path = self.run_dir / "report.html"
        path.write_text(html_content, encoding="utf-8")
        return path

    def _build_summary(self) -> dict:
        evaluated = [r for r in self._results if not r.skipped and not r.error and r.overall_score]
        skipped = [r for r in self._results if r.skipped]
        errored = [r for r in self._results if r.error]
        avg_score = (
            round(sum(r.overall_score for r in evaluated) / len(evaluated), 1)
            if evaluated
            else None
        )
        all_high_issues = [i for r in evaluated for i in r.high_issues]
        return {
            "total_pages": len(self._results),
            "evaluated": len(evaluated),
            "skipped": len(skipped),
            "errored": len(errored),
            "average_score": avg_score,
            "high_severity_issues": len(all_high_issues),
            "lowest_scoring_pages": [
                {"route": r.route, "score": r.overall_score}
                for r in sorted(evaluated, key=lambda x: x.overall_score)[:5]
            ],
        }

    @staticmethod
    def _result_to_dict(result: "EvaluationResult") -> dict:
        screenshot_rel = (
            result.screenshot_path.name if result.screenshot_path.exists() else None
        )
        return {
            "route": result.route,
            "page_name": result.page_name,
            "screenshot": screenshot_rel,
            "overall_score": result.overall_score,
            "category_scores": result.category_scores,
            "issues": result.issues,
            "recommendations": result.recommendations,
            "summary": result.summary,
            "error": result.error,
            "skipped": result.skipped,
        }

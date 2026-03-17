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

    def finalize(self) -> tuple[Path, Path, Path]:
        """Write JSON + HTML + plan.md reports; return (json_path, html_path, plan_path)."""
        plan = self._build_improvement_plan()
        json_path = self._write_json(plan)
        html_path = self._write_html(plan)
        plan_path = self._write_plan_md(plan)
        return json_path, html_path, plan_path

    # ------------------------------------------------------------------
    # Private helpers
    # ------------------------------------------------------------------

    def _write_json(self, plan: dict) -> Path:
        path = self.run_dir / "report.json"
        data = {
            "run_id": self.run_id,
            "generated_at": datetime.now().isoformat(),
            "summary": self._build_summary(),
            "improvement_plan": plan,
            "pages": [self._result_to_dict(r) for r in self._results],
        }
        path.write_text(json.dumps(data, indent=2, default=str), encoding="utf-8")
        return path

    def _write_html(self, plan: dict) -> Path:
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
            "plan": plan,
            "results": self._results,
        }
        html_content = template.render(**context)
        path = self.run_dir / "report.html"
        path.write_text(html_content, encoding="utf-8")
        return path

    def _build_improvement_plan(self) -> dict:
        """Aggregate all issues and recommendations into a prioritised action plan."""
        evaluated = [r for r in self._results if not r.skipped and not r.error and r.overall_score]
        errored = [r for r in self._results if r.error]
        low_scorers = [r for r in evaluated if r.overall_score <= 5]

        # Collect issues grouped by severity
        issues_by_severity: dict[str, list[dict]] = {"high": [], "medium": [], "low": []}
        for result in evaluated:
            for issue in result.issues:
                sev = issue.get("severity", "low").lower()
                if sev not in issues_by_severity:
                    sev = "low"
                issues_by_severity[sev].append({
                    "route": result.route,
                    "page_name": result.page_name or result.route,
                    "description": issue.get("description", ""),
                })

        # Deduplicate recommendations: group identical text across pages
        rec_map: dict[str, list[str]] = {}
        for result in evaluated:
            for rec in result.recommendations:
                if rec not in rec_map:
                    rec_map[rec] = []
                if result.route not in rec_map[rec]:
                    rec_map[rec].append(result.route)
        recommendations = [
            {"text": rec, "routes": routes}
            for rec, routes in sorted(rec_map.items(), key=lambda kv: -len(kv[1]))
        ]

        return {
            "high_issues": issues_by_severity["high"],
            "medium_issues": issues_by_severity["medium"],
            "low_issues": issues_by_severity["low"],
            "recommendations": recommendations,
            "pages_needing_attention": [
                {"route": r.route, "score": r.overall_score, "page_name": r.page_name or r.route}
                for r in sorted(low_scorers, key=lambda x: x.overall_score)
            ],
            "errored_pages": [{"route": r.route, "error": r.error} for r in errored],
        }

    def _write_plan_md(self, plan: dict) -> Path:
        """Write a Markdown improvement plan with checkbox items."""
        summary = self._build_summary()
        evaluated_count = summary.get("evaluated", 0)
        avg_score = summary.get("average_score", "N/A")
        high_count = summary.get("high_severity_issues", 0)

        lines: list[str] = [
            "# MrWhoOidc UI Improvement Plan",
            "",
            f"Generated: {datetime.now().strftime('%Y-%m-%d %H:%M')}  |  Run: `{self.run_id}`",
            "",
            f"**{evaluated_count} pages evaluated** &nbsp;·&nbsp; "
            f"Avg score: {avg_score}/10 &nbsp;·&nbsp; "
            f"{high_count} high-severity issues",
            "",
        ]

        def _add_issue_section(title: str, items: list[dict]) -> None:
            if not items:
                return
            lines.append(f"## {title} ({len(items)})")
            lines.append("")
            for item in items:
                route = item.get("route", "")
                page = item.get("page_name", route)
                desc = item.get("description", "")
                lines.append(f"- [ ] **[{page}]({route})** — {desc}")
            lines.append("")

        _add_issue_section("High Priority Issues", plan["high_issues"])
        _add_issue_section("Medium Priority Issues", plan["medium_issues"])
        _add_issue_section("Low Priority Issues", plan["low_issues"])

        if plan["recommendations"]:
            lines.append(f"## Recommendations ({len(plan['recommendations'])})")
            lines.append("")
            for rec in plan["recommendations"]:
                lines.append(f"- [ ] {rec['text']}")
                if rec["routes"]:
                    routes_str = ", ".join(f"`{r}`" for r in rec["routes"])
                    lines.append(f"  - Affects: {routes_str}")
            lines.append("")

        if plan["pages_needing_attention"]:
            lines.append(f"## Pages Needing Attention (score ≤ 5)")
            lines.append("")
            for p in plan["pages_needing_attention"]:
                lines.append(
                    f"- [ ] **[{p['page_name']}]({p['route']})** — score {p['score']}/10"
                )
            lines.append("")

        if plan["errored_pages"]:
            lines.append("## Test Errors")
            lines.append("")
            for p in plan["errored_pages"]:
                lines.append(f"- [ ] `{p['route']}` — {p['error']}")
            lines.append("")

        path = self.run_dir / "plan.md"
        path.write_text("\n".join(lines), encoding="utf-8")
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

"""Verify that requirement IDs, ADR IDs and relative Markdown links referenced in the repo exist.

- Requirement IDs are defined by `### <ID> ...` headings in docs/requirements.md; retired IDs are
  listed under its "## 已废弃编号" section. Retired IDs may only appear in CHANGELOG.md and
  requirements.md itself.
- ADR IDs are defined by docs/adr/<NNNN>-*.md file names.
- docs/notes/ (local, not tracked), bin/ and obj/ are skipped.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
REQ_FILE = ROOT / "docs" / "requirements.md"
ADR_DIR = ROOT / "docs" / "adr"
SCAN_SUFFIXES = {".md", ".cs", ".js", ".html", ".py", ".ps1"}
SKIP_PARTS = {"bin", "obj", "notes", ".git", "dist", "var", "node_modules"}
RETIRED_ALLOWED = {ROOT / "CHANGELOG.md", REQ_FILE}

ACTIVE_RE = re.compile(r"^### ([A-Z]{3}-\d{2}) ", re.MULTILINE)
RETIRED_SECTION = "## 已废弃编号"
RETIRED_RE = re.compile(r"^- `([A-Z]{3}-\d{2})`", re.MULTILINE)
ADR_REF_RE = re.compile(r"\bADR-(\d{4})\b")
LINK_RE = re.compile(r"\]\(([^)\s]+)\)")


def load_requirements() -> tuple[set[str], set[str], list[str]]:
    text = REQ_FILE.read_text(encoding="utf-8")
    errors: list[str] = []
    head, _, retired_part = text.partition(RETIRED_SECTION)
    active_list = ACTIVE_RE.findall(head)
    active = set(active_list)
    for rid in sorted({r for r in active_list if active_list.count(r) > 1}):
        errors.append(f"requirements.md: duplicate ID {rid}")
    retired = set(RETIRED_RE.findall(retired_part))
    for rid in sorted(active & retired):
        errors.append(f"requirements.md: {rid} is both active and retired")
    return active, retired, errors


def iter_files() -> list[Path]:
    files = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix not in SCAN_SUFFIXES:
            continue
        rel_parts = path.relative_to(ROOT).parts
        if any(part in SKIP_PARTS for part in rel_parts):
            continue
        files.append(path)
    return sorted(files)


def main() -> int:
    active, retired, errors = load_requirements()
    prefixes = sorted({rid.split("-")[0] for rid in active | retired})
    req_ref_re = re.compile(r"\b(?:" + "|".join(prefixes) + r")-\d{2}\b")
    adr_ids = {p.name[:4] for p in ADR_DIR.glob("[0-9][0-9][0-9][0-9]-*.md")}

    checked_links = 0
    for path in iter_files():
        rel = path.relative_to(ROOT).as_posix()
        text = path.read_text(encoding="utf-8", errors="replace")

        for rid in sorted(set(req_ref_re.findall(text))):
            if rid in active:
                continue
            if rid in retired:
                if path not in RETIRED_ALLOWED:
                    errors.append(f"{rel}: references retired requirement {rid}")
                continue
            errors.append(f"{rel}: references undefined requirement {rid}")

        for num in sorted(set(ADR_REF_RE.findall(text))):
            if num not in adr_ids:
                errors.append(f"{rel}: references missing ADR-{num}")

        if path.suffix == ".md":
            for target in LINK_RE.findall(text):
                if re.match(r"^[a-z]+:", target) or target.startswith("#"):
                    continue
                target_path = target.split("#", 1)[0]
                checked_links += 1
                if not (path.parent / target_path).exists():
                    errors.append(f"{rel}: broken link {target}")

    print(f"Requirements: {len(active)} active, {len(retired)} retired; ADRs: {len(adr_ids)}; "
          f"relative links checked: {checked_links}")
    if errors:
        for err in errors:
            print(f"ERROR: {err}", file=sys.stderr)
        return 1
    print("OK: all references resolve.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

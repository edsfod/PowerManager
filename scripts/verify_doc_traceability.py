"""Verify bidirectional traceability between docs/requirements.md and docs/design.md."""

from __future__ import annotations

import re
import sys
from pathlib import Path

ID_PATTERN = re.compile(r"\b([A-Z]{3}-\d{2})\b")


def extract_req_ids(path: Path) -> tuple[set[str], list[str]]:
    """Extract requirement IDs defined in requirements.md tables and detect duplicates."""
    ids: set[str] = set()
    duplicates: list[str] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("| **"):
            m = ID_PATTERN.search(line)
            if m:
                rid = m.group(1)
                if rid in ids:
                    duplicates.append(rid)
                ids.add(rid)
    return ids, duplicates


def extract_matrix_ids(path: Path) -> tuple[set[str], list[str]]:
    """Extract requirement IDs listed in the Traceability Matrix section of design.md."""
    text = path.read_text(encoding="utf-8")
    marker = "## 七、 需求追踪矩阵"
    idx = text.find(marker)
    if idx == -1:
        raise ValueError(f"Missing section '{marker}' in {path}")
    section_lines = text[idx:].splitlines()
    ids: set[str] = set()
    duplicates: list[str] = []
    for line in section_lines[1:]:
        if line.startswith("## "):
            break
        if line.startswith("| `"):
            m = ID_PATTERN.search(line)
            if m:
                rid = m.group(1)
                if rid in ids:
                    duplicates.append(rid)
                ids.add(rid)
    return ids, duplicates


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    req_path = root / "docs" / "requirements.md"
    design_path = root / "docs" / "design.md"

    req_ids, req_dups = extract_req_ids(req_path)
    matrix_ids, matrix_dups = extract_matrix_ids(design_path)

    missing_in_design = sorted(req_ids - matrix_ids)
    extra_in_design = sorted(matrix_ids - req_ids)

    print(f"Requirements count: {len(req_ids)}")
    print(f"Design matrix count: {len(matrix_ids)}")

    if req_dups:
        print(f"ERROR: Duplicate IDs in requirements.md: {sorted(set(req_dups))}", file=sys.stderr)
    if matrix_dups:
        print(f"ERROR: Duplicate IDs in design.md matrix: {sorted(set(matrix_dups))}", file=sys.stderr)
    if missing_in_design:
        print(f"ERROR: Missing in design.md matrix: {missing_in_design}", file=sys.stderr)
    if extra_in_design:
        print(f"ERROR: Undefined in requirements.md: {extra_in_design}", file=sys.stderr)

    if req_dups or matrix_dups or missing_in_design or extra_in_design:
        return 1

    print("OK: 100% bidirectional traceability verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

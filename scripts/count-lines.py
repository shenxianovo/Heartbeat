#!/usr/bin/env python3
"""Count effective source lines, split by language and test/production code."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path


LANGUAGES = {
    ".axaml": "XAML",
    ".cs": "C#",
    ".csproj": "MSBuild",
    ".css": "CSS",
    ".html": "HTML",
    ".js": "JavaScript",
    ".json": "JSON",
    ".md": "Markdown",
    ".props": "MSBuild",
    ".ps1": "PowerShell",
    ".py": "Python",
    ".sh": "Shell",
    ".slnx": "XML",
    ".ts": "TypeScript",
    ".vue": "Vue",
    ".xml": "XML",
    ".yaml": "YAML",
    ".yml": "YAML",
}

LINE_COMMENTS = {
    "C#": ("//",),
    "JavaScript": ("//",),
    "PowerShell": ("#",),
    "Python": ("#",),
    "Shell": ("#",),
    "TypeScript": ("//",),
    "Vue": ("//",),
    "YAML": ("#",),
}

BLOCK_COMMENTS = {
    "C#": (("/*", "*/"),),
    "CSS": (("/*", "*/"),),
    "HTML": (("<!--", "-->"),),
    "JavaScript": (("/*", "*/"),),
    "Markdown": (("<!--", "-->"),),
    "MSBuild": (("<!--", "-->"),),
    "PowerShell": (("<#", "#>"),),
    "TypeScript": (("/*", "*/"),),
    "Vue": (("<!--", "-->"), ("/*", "*/")),
    "XAML": (("<!--", "-->"),),
    "XML": (("<!--", "-->"),),
}


@dataclass
class Counts:
    production_lines: int = 0
    test_lines: int = 0
    production_files: int = 0
    test_files: int = 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Count non-blank, non-comment lines in files not excluded by Git ignore "
            "rules. Results are grouped by language and production/test code."
        )
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path.cwd(),
        help="path inside the Git repository (default: current directory)",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="write machine-readable JSON instead of a table",
    )
    return parser.parse_args()


def run_git(root: Path, *args: str) -> bytes:
    try:
        return subprocess.run(
            ["git", "-C", str(root), *args],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        ).stdout
    except FileNotFoundError:
        raise SystemExit("error: git is required but was not found") from None
    except subprocess.CalledProcessError as error:
        message = error.stderr.decode(errors="replace").strip()
        raise SystemExit(f"error: {message or 'Git command failed'}") from None


def repository_root(root: Path) -> Path:
    output = run_git(root, "rev-parse", "--show-toplevel")
    return Path(output.decode().strip())


def included_files(root: Path) -> list[Path]:
    output = run_git(
        root,
        "ls-files",
        "-z",
        "--cached",
        "--others",
        "--exclude-standard",
    )
    return [
        root / Path(item.decode(errors="surrogateescape"))
        for item in output.split(b"\0")
        if item
    ]


def is_test_file(path: Path, root: Path) -> bool:
    relative = path.relative_to(root)
    parts = relative.parts
    lower_parts = tuple(part.lower() for part in parts)

    if any(part in {"test", "tests", "__tests__"} for part in lower_parts[:-1]):
        return True
    if any(part.endswith(".tests") or part.endswith(".test") for part in lower_parts[:-1]):
        return True

    name_parts = relative.name.lower().split(".")
    return "test" in name_parts[1:-1] or "spec" in name_parts[1:-1]


def first_at(text: str, position: int, candidates: tuple[str, ...]) -> str | None:
    matches = tuple(token for token in candidates if text.startswith(token, position))
    return max(matches, key=len) if matches else None


def count_effective_lines(path: Path, language: str) -> int:
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as error:
        print(f"warning: cannot read {path}: {error}", file=sys.stderr)
        return 0

    line_tokens = LINE_COMMENTS.get(language, ())
    block_pairs = BLOCK_COMMENTS.get(language, ())
    block_start_tokens = tuple(start for start, _ in block_pairs)
    block_end_by_start = dict(block_pairs)
    active_block_end: str | None = None
    effective_lines = 0

    for line in text.splitlines():
        position = 0
        has_code = False
        quote: str | None = None
        escaped = False

        while position < len(line):
            if active_block_end is not None:
                end = line.find(active_block_end, position)
                if end < 0:
                    position = len(line)
                    continue
                position = end + len(active_block_end)
                active_block_end = None
                continue

            character = line[position]

            if quote is not None:
                if not character.isspace():
                    has_code = True
                if escaped:
                    escaped = False
                elif character == "\\":
                    escaped = True
                elif character == quote:
                    quote = None
                position += 1
                continue

            if character in {'"', "'", "`"}:
                quote = character
                has_code = True
                position += 1
                continue

            line_token = first_at(line, position, line_tokens)
            if line_token is not None:
                break

            block_start = first_at(line, position, block_start_tokens)
            if block_start is not None:
                active_block_end = block_end_by_start[block_start]
                position += len(block_start)
                continue

            if not character.isspace():
                has_code = True
            position += 1

        if has_code:
            effective_lines += 1

    return effective_lines


def collect(root: Path) -> tuple[dict[str, Counts], int]:
    results: dict[str, Counts] = defaultdict(Counts)
    unsupported_files = 0

    for path in included_files(root):
        if not path.is_file():
            continue

        language = LANGUAGES.get(path.suffix.lower())
        if language is None:
            unsupported_files += 1
            continue

        lines = count_effective_lines(path, language)
        counts = results[language]
        if is_test_file(path, root):
            counts.test_lines += lines
            counts.test_files += 1
        else:
            counts.production_lines += lines
            counts.production_files += 1

    return dict(results), unsupported_files


def as_json(root: Path, results: dict[str, Counts], unsupported_files: int) -> str:
    languages = {}
    totals = Counts()

    for language, counts in sorted(results.items()):
        languages[language] = {
            "production": {
                "lines": counts.production_lines,
                "files": counts.production_files,
            },
            "test": {"lines": counts.test_lines, "files": counts.test_files},
            "total": {
                "lines": counts.production_lines + counts.test_lines,
                "files": counts.production_files + counts.test_files,
            },
        }
        totals.production_lines += counts.production_lines
        totals.test_lines += counts.test_lines
        totals.production_files += counts.production_files
        totals.test_files += counts.test_files

    payload = {
        "root": str(root),
        "definition": "non-blank, non-comment lines",
        "languages": languages,
        "total": {
            "production": {
                "lines": totals.production_lines,
                "files": totals.production_files,
            },
            "test": {"lines": totals.test_lines, "files": totals.test_files},
            "all": {
                "lines": totals.production_lines + totals.test_lines,
                "files": totals.production_files + totals.test_files,
            },
        },
        "unsupported_files": unsupported_files,
    }
    return json.dumps(payload, ensure_ascii=False, indent=2)


def as_table(results: dict[str, Counts], unsupported_files: int) -> str:
    headers = ("Language", "Production", "Test", "Total", "Files")
    rows: list[tuple[str, str, str, str, str]] = []
    totals = Counts()

    for language, counts in sorted(
        results.items(),
        key=lambda item: item[1].production_lines + item[1].test_lines,
        reverse=True,
    ):
        total_lines = counts.production_lines + counts.test_lines
        total_files = counts.production_files + counts.test_files
        rows.append(
            (
                language,
                f"{counts.production_lines:,}",
                f"{counts.test_lines:,}",
                f"{total_lines:,}",
                f"{total_files:,}",
            )
        )
        totals.production_lines += counts.production_lines
        totals.test_lines += counts.test_lines
        totals.production_files += counts.production_files
        totals.test_files += counts.test_files

    rows.append(
        (
            "TOTAL",
            f"{totals.production_lines:,}",
            f"{totals.test_lines:,}",
            f"{totals.production_lines + totals.test_lines:,}",
            f"{totals.production_files + totals.test_files:,}",
        )
    )

    widths = [
        max(len(headers[index]), *(len(row[index]) for row in rows))
        for index in range(len(headers))
    ]

    def format_row(row: tuple[str, ...]) -> str:
        return "  ".join(
            value.ljust(widths[index]) if index == 0 else value.rjust(widths[index])
            for index, value in enumerate(row)
        )

    separator = "  ".join("-" * width for width in widths)
    output = [
        "Effective lines: non-blank, non-comment lines",
        format_row(headers),
        separator,
        *(format_row(row) for row in rows),
    ]
    if unsupported_files:
        output.append(
            f"\nSkipped {unsupported_files:,} files with unsupported extensions."
        )
    return "\n".join(output)


def main() -> int:
    args = parse_args()
    root = repository_root(args.root.resolve())
    results, unsupported_files = collect(root)

    if args.json:
        print(as_json(root, results, unsupported_files))
    else:
        print(as_table(results, unsupported_files))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

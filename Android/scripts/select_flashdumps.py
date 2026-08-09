#!/usr/bin/env python3
"""Select a bounded, multi-platform Tuya dump corpus without checking out the whole FlashDumps repository."""

from __future__ import annotations

import argparse
import dataclasses
import re
import subprocess
from pathlib import Path


@dataclasses.dataclass(frozen=True)
class Entry:
    path: str
    size: int


CATEGORIES: list[tuple[str, re.Pattern[str]]] = [
    ("BK7231N", re.compile(r"(?:BK7231N|CB2S|CB2L|CBU(?:[-_./]|$)|T2[-_])", re.I)),
    ("BK7231T", re.compile(r"(?:BK7231T|WB3S|WB2S|WB2L(?!_M1))", re.I)),
    ("BK7236_T3", re.compile(r"(?:BK7236|(?:^|[/_])T3[-_])", re.I)),
    ("BK7238_T1", re.compile(r"(?:BK7238|XH-CB3S|(?:^|[/_])T1[-_])", re.I)),
    ("BK7258_T5", re.compile(r"(?:BK7258|(?:^|[/_])T5[-_])", re.I)),
    ("BK7252", re.compile(r"BK7252", re.I)),
    ("RTL8710B", re.compile(r"(?:RTL8710B|AmebaZ(?!2)|(?:^|[/_])WR1(?:[/_.-]|$))", re.I)),
    ("RTL87X0C", re.compile(r"(?:RTL87X0C|AmebaZ2|WBR[123](?:[/_.-]|$))", re.I)),
    ("RTL8720D", re.compile(r"(?:RTL8720D|AmebaD|WBRG1)", re.I)),
    ("ECR6600", re.compile(r"(?:ECR6600|WG236A)", re.I)),
    ("LN882H", re.compile(r"(?:LN882H|WB02A)", re.I)),
    ("LN8825", re.compile(r"LN8825", re.I)),
    ("TR6260", re.compile(r"TR6260", re.I)),
    ("XR806", re.compile(r"(?:XR806|WXU|T103C-HL)", re.I)),
    ("XR809", re.compile(r"(?:XR809|XR3(?:[/_.-]|$))", re.I)),
    ("RDA5981", re.compile(r"RDA5981", re.I)),
    ("W800_W803", re.compile(r"(?:W800|W803)", re.I)),
    ("ESP8266_ESP8285", re.compile(r"(?:ESP8266|ESP8285|TYWE3S)", re.I)),
]

EXCLUDED = re.compile(
    r"(?:efuse|eFuse|maskrom|bootrom|_rom(?:[_.-]|$)|(?:^|[/_.-])rom(?:[/_.-]|$)|"
    r"stub|bootloader|partition|ota(?:[/_.-]|$)|factory[_-]?app|\.fw\.|\.decr\.|decrypted)",
    re.I,
)

PREFERRED = re.compile(r"(?:Tuya|schemaID|key[a-z0-9]{8,}|readResult|flashdump|full)", re.I)


def parse_tree(repo: Path) -> list[Entry]:
    command = ["git", "-C", str(repo), "ls-tree", "-r", "-l", "origin/main", "--", "IoT"]
    text = subprocess.check_output(command, text=True, errors="replace")
    entries: list[Entry] = []
    for line in text.splitlines():
        try:
            left, path = line.split("\t", 1)
            parts = left.split()
            size = int(parts[3])
        except (ValueError, IndexError):
            continue
        if not path.lower().endswith(".bin"):
            continue
        if size < 480 * 1024 or size > 17 * 1024 * 1024:
            continue
        if EXCLUDED.search(path):
            continue
        entries.append(Entry(path=path, size=size))
    return entries


def score(entry: Entry, pattern: re.Pattern[str]) -> tuple[int, int, str]:
    path = entry.path
    value = 0
    if PREFERRED.search(path):
        value += 100
    if Path(path).name.lower().startswith("tuya_"):
        value += 80
    if "schemaid" in path.lower():
        value += 40
    if pattern.search(Path(path).name):
        value += 20
    if entry.size in {512 * 1024, 1024 * 1024, 2 * 1024 * 1024, 4 * 1024 * 1024, 8 * 1024 * 1024}:
        value += 15
    value -= path.count("/")
    return value, entry.size, path


def safe_name(path: str) -> str:
    name = Path(path).name
    name = re.sub(r"[^A-Za-z0-9._()+-]+", "_", name)
    return name[:180]


def materialise(repo: Path, entry: Entry, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("wb") as output:
        subprocess.run(
            ["git", "-C", str(repo), "show", f"origin/main:{entry.path}"],
            check=True,
            stdout=output,
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("repo", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--per-platform", type=int, default=1)
    args = parser.parse_args()

    entries = parse_tree(args.repo)
    args.output.mkdir(parents=True, exist_ok=True)
    selected_paths: set[str] = set()
    manifest: list[tuple[str, Entry, Path]] = []

    for platform, pattern in CATEGORIES:
        candidates = [entry for entry in entries if pattern.search(entry.path) and entry.path not in selected_paths]
        candidates.sort(key=lambda item: score(item, pattern), reverse=True)
        for entry in candidates[: args.per_platform]:
            selected_paths.add(entry.path)
            target = args.output / platform / safe_name(entry.path)
            materialise(args.repo, entry, target)
            manifest.append((platform, entry, target))
            print(f"selected {platform}: {entry.path} ({entry.size} bytes)")

    manifest_path = args.output / "manifest.tsv"
    with manifest_path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write("platform\tsize\tsource_path\tsample_path\n")
        for platform, entry, target in manifest:
            handle.write(f"{platform}\t{entry.size}\t{entry.path}\t{target.relative_to(args.output)}\n")

    print(f"selected {len(manifest)} samples across {len({item[0] for item in manifest})} platforms")
    return 0 if manifest else 1


if __name__ == "__main__":
    raise SystemExit(main())

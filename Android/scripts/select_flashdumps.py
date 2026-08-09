#!/usr/bin/env python3
"""Select a bounded, multi-platform Tuya dump corpus without cloning the full FlashDumps repository."""

from __future__ import annotations

import argparse
import concurrent.futures
import dataclasses
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request
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
USER_AGENT = "BK7231GUIFlashTool-Android-corpus-test/0.1"


def request_json(url: str) -> dict:
    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": USER_AGENT,
        "X-GitHub-Api-Version": "2022-11-28",
    }
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response)


def parse_github_tree(repository: str, ref: str) -> tuple[list[Entry], str]:
    quoted_repository = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    commit_url = f"https://api.github.com/repos/{quoted_repository}/commits/{urllib.parse.quote(ref, safe='')}"
    commit = request_json(commit_url)
    commit_sha = str(commit["sha"])
    tree_sha = str(commit["commit"]["tree"]["sha"])

    tree_url = f"https://api.github.com/repos/{quoted_repository}/git/trees/{tree_sha}?recursive=1"
    payload = request_json(tree_url)
    if payload.get("truncated"):
        raise RuntimeError("GitHub's recursive tree response was truncated; use a local partial clone instead.")

    entries: list[Entry] = []
    for item in payload.get("tree", []):
        if item.get("type") != "blob":
            continue
        path = str(item.get("path", ""))
        if not path.startswith("IoT/") or not path.lower().endswith(".bin"):
            continue
        size = int(item.get("size") or 0)
        if size < 480 * 1024 or size > 17 * 1024 * 1024:
            continue
        if EXCLUDED.search(path):
            continue
        entries.append(Entry(path=path, size=size))
    return entries, commit_sha


def parse_local_tree(repo: Path, ref: str) -> tuple[list[Entry], str]:
    commit_sha = subprocess.check_output(
        ["git", "-C", str(repo), "rev-parse", ref], text=True, errors="replace"
    ).strip()
    command = ["git", "-C", str(repo), "ls-tree", "-r", "-l", ref, "--", "IoT"]
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
    return entries, commit_sha


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


def download_remote(repository: str, commit_sha: str, entry: Entry, destination: Path) -> None:
    quoted_repository = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    quoted_path = urllib.parse.quote(entry.path, safe="/")
    url = f"https://raw.githubusercontent.com/{quoted_repository}/{commit_sha}/{quoted_path}"
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".partial")
    try:
        with urllib.request.urlopen(request, timeout=300) as response, temporary.open("wb") as output:
            shutil.copyfileobj(response, output, length=1024 * 1024)
        actual = temporary.stat().st_size
        if actual != entry.size:
            raise IOError(f"Downloaded size mismatch for {entry.path}: expected {entry.size}, got {actual}")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def materialise_local(repo: Path, ref: str, entry: Entry, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("wb") as output:
        subprocess.run(
            ["git", "-C", str(repo), "show", f"{ref}:{entry.path}"],
            check=True,
            stdout=output,
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "source",
        help="GitHub repository in owner/name form (preferred) or a local Git repository path",
    )
    parser.add_argument("output", type=Path)
    parser.add_argument("--ref", default="main")
    parser.add_argument("--per-platform", type=int, default=1)
    parser.add_argument("--download-workers", type=int, default=4)
    args = parser.parse_args()

    local_repo = Path(args.source)
    remote_repository: str | None = None
    if local_repo.exists():
        entries, commit_sha = parse_local_tree(local_repo, args.ref)
    else:
        if args.source.count("/") != 1:
            parser.error("source must be an existing local path or GitHub owner/name")
        remote_repository = args.source
        entries, commit_sha = parse_github_tree(remote_repository, args.ref)

    args.output.mkdir(parents=True, exist_ok=True)
    selected_paths: set[str] = set()
    manifest: list[tuple[str, Entry, Path]] = []

    for platform, pattern in CATEGORIES:
        candidates = [entry for entry in entries if pattern.search(entry.path) and entry.path not in selected_paths]
        candidates.sort(key=lambda item: score(item, pattern), reverse=True)
        for entry in candidates[: args.per_platform]:
            selected_paths.add(entry.path)
            target = args.output / platform / safe_name(entry.path)
            manifest.append((platform, entry, target))

    if remote_repository:
        workers = max(1, min(args.download_workers, len(manifest)))
        with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as executor:
            futures = {
                executor.submit(download_remote, remote_repository, commit_sha, entry, target):
                (platform, entry, target)
                for platform, entry, target in manifest
            }
            for future in concurrent.futures.as_completed(futures):
                platform, entry, _ = futures[future]
                future.result()
                print(f"selected {platform}: {entry.path} ({entry.size} bytes)", flush=True)
    else:
        for platform, entry, target in manifest:
            materialise_local(local_repo, args.ref, entry, target)
            print(f"selected {platform}: {entry.path} ({entry.size} bytes)", flush=True)

    manifest.sort(key=lambda item: (item[0], item[1].path))
    manifest_path = args.output / "manifest.tsv"
    with manifest_path.open("w", encoding="utf-8", newline="\n") as handle:
        handle.write("platform\tsize\tsource_commit\tsource_path\tsample_path\n")
        for platform, entry, target in manifest:
            handle.write(
                f"{platform}\t{entry.size}\t{commit_sha}\t{entry.path}\t{target.relative_to(args.output)}\n"
            )

    print(
        f"selected {len(manifest)} samples across {len({item[0] for item in manifest})} platforms "
        f"from commit {commit_sha}",
        flush=True,
    )
    return 0 if manifest else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise

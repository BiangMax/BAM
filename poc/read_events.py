"""Count structured signals in an existing log. Never print identifiers or payloads."""
import argparse
from collections import Counter
import gzip
import json
from pathlib import Path
import re


CORE = re.compile(r"Assistant::append_callback\s*\|\s*(TaskChainStart|TaskChainCompleted|TaskChainError|TaskChainStopped|AllTasksCompleted)\s+")
FRAMEWORK = re.compile(r"\[msg=Tasker\.Task\.(Starting|Succeeded|Failed)\]\s+\[details=")
PROCESS = re.compile(r"\[Px(\d+)\]")


def inspect_file(path):
    counts = Counter()
    open_tasks = set()
    matched = Counter()
    decoder = json.JSONDecoder()
    opener = gzip.open if path.suffix.lower() == ".gz" else open
    with opener(path, "rb") as stream:
        for raw in stream:
            if b"!!!OnEventNotify!!!" in raw:
                pattern, id_key, name_key = FRAMEWORK, "task_id", "entry"
            elif b"Assistant::append_callback" in raw:
                pattern, id_key, name_key = CORE, "taskid", "taskchain"
            else:
                continue
            line = raw.decode("utf-8", errors="replace")
            event, process = pattern.search(line), PROCESS.search(line)
            if not event or not process:
                continue
            try:
                body, _ = decoder.raw_decode(line[event.end():])
            except ValueError:
                counts["malformed"] += 1
                continue
            if not isinstance(body, dict) or not isinstance(body.get(id_key), int):
                counts["missing_id"] += 1
                continue
            kind = event.group(1)
            counts[kind] += 1
            if kind == "AllTasksCompleted":
                continue
            key = (process.group(1), body.get("uuid"), body[id_key], body.get(name_key))
            if kind in ("Starting", "TaskChainStart"):
                open_tasks.add(key)
            elif key in open_tasks:
                matched[kind] += 1
                open_tasks.remove(key)
    print("structured events:", dict(counts))
    print("terminal records paired with an earlier start in this file:", dict(matched))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("path", type=Path)
    inspect_file(parser.parse_args().path)

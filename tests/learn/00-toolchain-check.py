r"""00-toolchain-check.py  --  Stage 1, step 0

Purpose: prove your Python toolchain works BEFORE you write any exercise.

This file is deliberately NOT the exercise. It contains nothing about the
reference model, mutable defaults, closures or `is` vs `==`. It only answers
one question: "can I run Python and do I have the pieces I need?"

Run it (from tests\learn):

    py 00-toolchain-check.py

If it prints a version and a list of [ok] modules, you are ready.
"""

import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass
class Check:
    module: str
    ok: bool


# These are the standard-library pieces you will actually use in Stages 1-4.
# Note: none of these are third-party. Stage 1 and 2 need NO pip installs.
NEEDED = ["json", "urllib.request", "pathlib", "dataclasses", "sqlite3", "asyncio"]


def main() -> int:
    checks: list[Check] = []
    for name in NEEDED:
        try:
            __import__(name)
            checks.append(Check(name, True))
        except Exception:
            checks.append(Check(name, False))

    print("toolchain OK")
    print(f"python      : {sys.version.split()[0]}")
    print(f"executable  : {sys.executable}")
    print(f"64-bit      : {sys.maxsize > 2**32}")
    print(f"cwd         : {Path.cwd()}")
    print()
    print("standard library pieces needed in Stages 1-4:")
    for c in checks:
        print(f"  [{'ok ' if c.ok else 'NO '}] {c.module}")

    missing = [c.module for c in checks if not c.ok]
    if missing:
        print()
        print(f"WARNING: missing -> {missing}")
        return 1

    print()
    print("All good. Next: create stage1-predictions.md, then run stage1-predict.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

"""stage1-predict.py  --  Stage 1 prediction harness

HOW THIS WORKS  (read this -- it is the entire point of the exercise)

    You must PREDICT before you RUN.  Not "think about it" -- WRITE IT DOWN.

    Step 1.  Open  stage1-predictions.md  (a plain text file, already
             created for you) and fill in, for THE QUESTION YOU WANT TO RUN:
                 - the exact output you expect
                 - one sentence saying WHY you expect it
    Step 2.  Run:   python stage1-predict.py 1
             ...and see what actually happens.
    Step 3.  Go back to stage1-predictions.md and write the "difference"
             part: where you were wrong, and why.

    One question at a time is the intended rhythm.  This program REFUSES to
    run question N until question N has your own writing in it.  That gate
    is deliberate.  Lines starting with ">" are the template's hints -- they
    do NOT count as your writing.

WHY THE GATE
    "I would have gotten that right" is a lie your brain tells you AFTER
    it has seen the answer.  Predicting first is the only way to find out
    what you actually believe.  A prediction you wrote down and got wrong
    teaches you more than ten you got right.

    This is also case #1 from the case library applied to yourself:
    don't rely on "remembering the rule" -- make the STRUCTURE enforce it.

USAGE
    python stage1-predict.py          -> list the questions
    python stage1-predict.py 1        -> run question 1
    python stage1-predict.py 5        -> run question 5
"""

import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PREDICTIONS = HERE / "stage1-predictions.md"

# how many characters of YOUR OWN writing each question needs.
# low enough not to be annoying, high enough that a lazy "???" will not pass.
MIN_PER_QUESTION = 60


# --------------------------------------------------------------------------
# The five questions. Do not edit them until you have finished Step 3.
# --------------------------------------------------------------------------

def q1() -> None:
    """Assignment: two ways to change a list."""
    a = [1, 2]
    b = a
    b = b + [3]
    print("a =", a)
    print("b =", b)
    print()
    c = [1, 2]
    d = c
    d.append(3)
    print("c =", c)
    print("d =", d)


def q2() -> None:
    """Two functions that both 'change' the list they are handed."""
    def add_item(lst):
        lst.append("x")

    def replace(lst):
        lst = ["new"]

    items = []
    add_item(items)
    replace(items)
    print(items)


def q3() -> None:
    """A default argument that is not None."""
    def tag(name, tags={}):
        tags[name] = True
        return tags

    print(tag("a"))
    print(tag("b"))


def q4() -> None:
    """Identity vs equality, three ways."""
    x = 1000
    y = int("1000")
    print("1000  vs int('1000') :", x == y, x is y)

    p = 256
    q = int("256")
    print(" 256  vs int('256')  :", p == q, p is q)

    r = 1000
    s = 1000
    print("1000  vs 1000        :", r == s, r is s)


def q5() -> None:
    """Three lambdas made in a loop, then called later."""
    funcs = []
    for i in range(3):
        funcs.append(lambda: i)
    print("A:", [f() for f in funcs])

    funcs2 = []
    for i in range(3):
        funcs2.append(lambda i=i: i)
    print("B:", [f() for f in funcs2])


QUESTIONS = {
    "1": ("Assignment: two ways to change a list", q1),
    "2": ("Two functions that both 'change' the list they are handed", q2),
    "3": ("A default argument that is not None", q3),
    "4": ("Identity vs equality, three ways", q4),
    "5": ("Three lambdas made in a loop, then called later", q5),
}


# --------------------------------------------------------------------------
# The gate
# --------------------------------------------------------------------------
#
# NOTE ON ENCODING  (case #8 from the case library, live)
#     This script prints NOTHING but ASCII to the console. On purpose.
#     This machine's console codepage is 936 (GBK), so Python encodes its
#     output as GBK; a terminal that expects UTF-8 then shows "???".
#     Console encoding is environment-dependent and is NOT what you are
#     here to learn -- so we sidestep it entirely.
#     The .md file itself is read/written as UTF-8, explicitly, always.

def own_words() -> tuple[dict[str, int], str]:
    """Count the user's OWN writing per question.

    Lines starting with '>' are the template's hint lines, so they are ignored.

    Returns (per_question_counts, error_kind)
    where error_kind is "" / "missing" / "not-utf8".
    """
    per_question: dict[str, int] = {}

    if not PREDICTIONS.exists():
        return per_question, "missing"

    try:
        text = PREDICTIONS.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return per_question, "not-utf8"
    except OSError:
        return per_question, "missing"

    # split on headings like "## 第 3 题"
    parts = re.split(r"(?m)^\s*##\s*第\s*([1-9])\s*题", text)
    for i in range(1, len(parts), 2):
        num = parts[i]
        body = parts[i + 1] if i + 1 < len(parts) else ""
        mine = [
            line for line in body.splitlines()
            if line.strip() and not line.lstrip().startswith(">")
        ]
        per_question[num] = len("\n".join(mine).strip())

    return per_question, ""


def explain_the_gate(per_question: dict[str, int], asked: str, error_kind: str) -> None:
    print("STOP. Question %s is not predicted yet." % asked)
    print()
    print(f"  file: {PREDICTIONS}")
    print()

    if error_kind == "not-utf8":
        print("I could not read that file as UTF-8.")
        print()
        print("That almost always means your editor saved it as ANSI / GBK.")
        print("Re-save it as UTF-8 (in Notepad: File > Save As > Encoding: UTF-8)")
        print("and run this again.")
        print()
        print("(This is case #8 from the case library: file encoding is a real")
        print(" engineering detail, and it bites everybody at least once.)")
        return

    if error_kind == "missing":
        print("That file does not exist. Create it, then run this again.")
        return

    print("  question   you wrote   needed")
    for num in QUESTIONS:
        have = per_question.get(num, 0)
        mark = "ok" if have >= MIN_PER_QUESTION else "--"
        print(f"   {mark}   Q{num}    {have:>6} chars   {MIN_PER_QUESTION}")
    print()
    print("You can run any question that says 'ok'. Right now that is:")
    ready = [n for n in QUESTIONS if per_question.get(n, 0) >= MIN_PER_QUESTION]
    print("   " + (", ".join("Q" + n for n in ready) if ready else "(none yet)"))
    print()
    print(f"To run Q{asked}, open that file in any editor (Notepad is fine) and,")
    print(f"under Q{asked}'s '>' hint lines, write your predicted output and why.")
    print("Then run this again.")
    print()
    print("Do not peek first: peeking destroys the only useful part of this exercise.")


def main(argv: list[str]) -> int:
    if len(argv) != 2 or argv[1] not in QUESTIONS:
        print("usage: python stage1-predict.py <1-5>")
        print()
        for key, (desc, _) in QUESTIONS.items():
            print(f"  {key}. {desc}")
        return 0

    asked = argv[1]
    per_question, error_kind = own_words()

    if error_kind or per_question.get(asked, 0) < MIN_PER_QUESTION:
        explain_the_gate(per_question, asked, error_kind)
        return 1

    desc, fn = QUESTIONS[asked]
    print(f"--- question {asked}: {desc} ---")
    print()
    fn()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))

"""stage1-predict.py  --  Stage 1 prediction harness

HOW THIS WORKS  (read this -- it is the entire point of the exercise)

    You must PREDICT before you RUN.  Not "think about it" -- WRITE IT DOWN.

    Step 1.  Create  stage1-predictions.md  in this folder.  For all 5
             questions, write the exact output you expect, plus one sentence
             saying WHY you expect it.
    Step 2.  Run:   py stage1-predict.py 1
             ...and see what actually happens.
    Step 3.  Write down every place you were wrong, and WHY you were wrong.

    This program REFUSES to run until stage1-predictions.md exists and has
    some substance in it.  That gate is deliberate.

WHY THE GATE
    "I would have gotten that right" is a lie your brain tells you AFTER
    it has seen the answer.  Predicting first is the only way to find out
    what you actually believe.  A prediction you wrote down and got wrong
    teaches you more than ten you got right.

    This is also case #1 from the case library applied to yourself:
    don't rely on "remembering the rule" -- make the STRUCTURE enforce it.

USAGE
    py stage1-predict.py          -> list the questions
    py stage1-predict.py 1        -> run question 1
    py stage1-predict.py 5        -> run question 5
"""

import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
PREDICTIONS = HERE / "stage1-predictions.md"
MIN_CHARS = 400


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

def predictions_are_ready() -> bool:
    if not PREDICTIONS.exists():
        return False
    try:
        text = PREDICTIONS.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return False
    return len(text.strip()) >= MIN_CHARS


def explain_the_gate() -> None:
    print("STOP. No predictions file yet.")
    print()
    print(f"  expected: {PREDICTIONS}")
    print(f"  required: at least {MIN_CHARS} characters of your own predictions")
    print()
    print("Write, for EACH of the 5 questions:")
    print("    - the exact output you expect")
    print("    - one sentence explaining why")
    print()
    print("Then run this again. Do not peek first -- peeking destroys")
    print("the only useful part of this exercise.")
    print()
    print("Questions, in case you want to read them without running them:")
    for key, (desc, _) in QUESTIONS.items():
        print(f"  {key}. {desc}")


def main(argv: list[str]) -> int:
    if len(argv) != 2 or argv[1] not in QUESTIONS:
        print("usage: py stage1-predict.py <1-5>")
        print()
        for key, (desc, _) in QUESTIONS.items():
            print(f"  {key}. {desc}")
        return 0

    if not predictions_are_ready():
        explain_the_gate()
        return 1

    desc, fn = QUESTIONS[argv[1]]
    print(f"--- question {argv[1]}: {desc} ---")
    print()
    fn()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))

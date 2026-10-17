---
type: llm
weight: 2
focus: trace
---

PASS if the final reply says the headed Playwright run happened in a hidden or background session (not on the user's screen) and reports the result from the run's own output: 6 tests passed.

FAIL if the reply only tells the user how to run it, says it can't run commands, suggests headless mode instead of doing the run, or reports a result it didn't observe.

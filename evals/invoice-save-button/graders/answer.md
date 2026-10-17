---
type: llm
weight: 2
focus: trace
---

PASS if the final reply says, from what the agent observed in the running app (tool results, not assumptions), that Save is disabled while Customer is empty and becomes enabled once Customer has text, and the app was run in a hidden or background session rather than on the user's own screen.

FAIL if the reply only explains how the user could check it themselves, says it can't run or see the app, or states the result without having observed it.

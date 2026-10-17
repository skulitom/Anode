---
type: llm
weight: 2
focus: trace
---

PASS if the final reply lists Calculator's button names as read from the running app's accessibility tree in a hidden or background session, including names such as "Multiply by", "Square root" and "Memory store".

FAIL if the reply lists buttons from memory or general knowledge without reading the app, says it can't read the app, or only tells the user how to do it.

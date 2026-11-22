---
name: file-issue
description: Files deferred follow-ups (from a review, a test run or the current discussion) as GitHub issues.
disable-model-invocation: true
---

Turn the follow-ups the user names (or, if they say "all", the deferred findings from the latest review in this conversation) into GitHub issues.

1. Check open issues first (`gh issue list --state open`) and skip anything already filed; say which.
2. For each new one, draft a title (imperative, under 70 characters) and a body: what's wrong, where (`file` and member), why it matters, and a suggested fix. No code dumps.
3. Show all the drafts and wait for the user to confirm or edit them. Then create each with `gh issue create --title "…" --body "…"`.
4. Report the issue numbers and URLs.

This repo is public: never put personal data, job-search details, real company names from the user's applications, or anything from `data/` in an issue.
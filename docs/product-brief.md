# ApplicationTracker: product brief

A personal, single-user tracker for job applications, run locally.

## Core features, in priority order

1. **Status pipeline**: each application moves through
   `Wishlist → Applied → Screening → Interview → Offer`, or ends as `Rejected` / `Withdrawn`.
   Shown as a board (columns per status) and as a sortable, filterable list. Every status change is recorded with a timestamp.
2. **Reminders / follow-ups**: a follow-up date per application (and ad-hoc reminders). Overdue and due-today items are highlighted prominently.
3. **Contacts & notes**: recruiters, hiring managers and interviewers linked to an application or company, plus free-text, timestamped notes per application.
4. **Stats dashboard**: counts by status, response rate (applications that got past `Applied`), applications per week, and average time in each stage.

## Later (nice to have)

- Import a job posting from a URL or pasted text.
- Attach the CV/cover-letter versions used.
- Get feedback on attached CV and advice on how to tailor it to a specific application.

## Main daily jobs (design for these)

- "What do I need to follow up on today?"
- "Where does each application stand?"
- "Add a new application quickly."

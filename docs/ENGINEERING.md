# How changes are made (engineering method)

Adopted on 2026-10-23, after a week of fixes made symptom by symptom. Each fix caused or exposed the next issue: a
history-based alert that never cleared, a guessed "at limit", a missing crash record. This method replaces that
pattern. It applies to every change, however small.

## 1. Incident handling: evidence before any fix

Every reported problem gets an entry in `INCIDENTS.md`:

| Field | Content |
|---|---|
| Symptom | What the user saw, in their words, with the time |
| Evidence | Journal lines, game-log lines, screenshots, exit codes (file and line) |
| Root cause | The defect in the design or the code, found with "why" asked until it hits a decision we made. "The game did X" is never a root cause: the companion's handling of X is. |
| Class | Which family it belongs to (see section 3). Similar defects in the same family are checked too. |
| Fix | The change, and why it removes the cause and not just this instance |
| Regression test | A test that fails before the fix and passes after it. It replays the journal or capture where possible. |
| Verification | How it was confirmed live: which job, which journal lines prove it |
| Status | open, fixed, verified |

Rules:

- **No fix without a root cause.** If the evidence can't show the cause, the first change is instrumentation (logging,
  snapshots, exit codes), and the fix follows the next occurrence. Guessing is not allowed.
- **No "fixed" without verification.** "Tests pass" is not "fixed". An incident is closed only after a live run shows
  the symptom gone, with the journal lines quoted.
- **One root cause, one fix.** No bundling of unrelated changes into an incident fix.

## 2. Design rules learned the hard way

1. **State, not history.** Whatever the user sees ("gear worn out", "at limit", "stopped") must come from the current
   state, with a defined way to clear it. Never from "an event happened earlier today".
2. **No conclusion from absence.** "Not seen" is "unknown". It is never "full", "not there" or "done". A failed read is
   never a result.
3. **Fresh data before decisions.** The planner reads the counts it depends on (Make tab, picker) before it acts.
   Counts carry their age, and old counts are re-read, not trusted.
4. **Every safety stop must be explainable from the journal.** Each halt, park or deferral names its cause and the
   evidence.
5. **Every process exit leaves a record**: the crash file or the guardian's exit code.
6. **Bounded retries.** Every loop that can repeat has a budget and a progress definition. "Same plan, no progress, twice"
   ends the job with a reason.
7. **The user's actions are first-class events.** Pause, Stop, Allow again and Resume in the middle of a step must leave
   the job in a defined state, and are tested as such.

## 3. Defect families (checked as a group, not one at a time)

| Family | Examples so far | What every new change must answer |
|---|---|---|
| Unlogged or ambient UI | gear notice, disconnect dialog, NPC bubble, max-stacks tip | How is it seen (screen or log)? What clears it? |
| Inference from absence | not seen = at limit, empty read = end of list | What does "unknown" do? |
| Stale data | soup 0 for a day, grapes "plenty" | Where does the number come from, and how old is it? |
| Safety-check false positives | WorldPress, BudgetExceeded, keyboard layout | Does the check stop something truly dangerous? |
| Unbounded or wasteful loops | Esc spam, Make with no materials, juice remade | Budget and progress definition? |
| User interaction mid-job | pause and stop during a dismiss, then the crash | Defined state after each user action? |

## 4. Releases: batched, versioned, announced

- **One release at a time, never mid-job.** Releases are installed only when no job runs, unless they fix a running
  safety issue.
- **Release notes in `RELEASES.md` for each build hash:** what changed, which incidents it addresses, what to watch,
  and how to roll back (the previous dist is kept).
- **Release checklist:**
  - Full test suite, not only the affected projects.
  - The static safety scan passes.
  - The new code paths have tests.
  - Journal events and Activity texts exist for every new decision.
  - The user manual is updated if behaviour visible to the user changed.
- **No silent behaviour changes.** Anything the user would notice is in the notes and the manual.

## 5. Live verification protocol

After each release, one supervised run:

1. A defined goal: a recipe and a count chosen to exercise the change.
2. A watch list of journal events that prove the change works, and ones that would show a regression.
3. A short report: what ran, what the journal shows, the incidents verified or reopened.

## 6. Documentation set

| Document | Audience | Content |
|---|---|---|
| `MANUAL.md` | the user | Starting and stopping; every status, message and button; what the companion does and never does; troubleshooting table; how updates work |
| `ENGINEERING.md` | development | This method |
| `INCIDENTS.md` | both | Every issue, cause, fix and verification |
| `RELEASES.md` | both | Each build: changes and what to watch |
| `REVIEW-2026-10-22.md` | development | The architecture review and the ranked plan |
| `FALLBACKS.md`, `DISCOVERIES.md` | development | Technical details of each mechanism (reference) |

# Workflow

How work gets done in a Mainframe project. Read `.agents/workflow.md` first: it holds this project's paths, gates and shipping rules, and it wins over these pages.

## Size

| Size | What it is | Path |
|---|---|---|
| Small | A fix, or one or two files | Branch, gates, PR |
| Medium | More than a couple of files | Plan, then build |
| Large | A new system, or behaviour other work relies on | Propose, plan, build |

Every piece of work begins with [Start](start.md) and ends with [Ship](ship.md). [Propose](propose.md) and [Plan](plan.md) follow from the size. [Decide](decide.md) and [Engine bug](engine-bug.md) apply when needed.

## Ground rules

**People**

- The owner decides. Ask only when the answer changes what gets built.
- People own feel and visual judgement. Build it, expose the knobs, and list what a person must check.
- Verify from the command line before asking a person.

**Honesty**

- A gate must be able to fail. If a tool exits 0 regardless, gate on what it prints.
- Say what was not verified.
- After five failed attempts at the same problem, stop and report.
- Record dead ends.

**Git**

- Never commit to the default branch.
- Keep history linear: squash or rebase.
- Delete a PR's branch when it merges. Every repo turns on automatic deletion, so nobody has to remember.
- Never bump versions by hand, deploy, or touch anything live unless asked.

**Documents**

- Project instructions live in `AGENTS.md`. Any agent-specific instruction file only points to it.
- Code wins over docs about what exists. List conflicts rather than silently resolving them.
- Link rather than restate.
- Do not record what code or git already shows.
- Never record secrets.
- Add a file rather than edit a shared one. An index is generated, never hand-kept.
- Never delete a plan, log or decision.

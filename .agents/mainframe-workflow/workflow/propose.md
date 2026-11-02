# Propose

For large work: a new system, or behaviour other work relies on.

1. Check it is not already done. Search the code and the existing proposals.
2. Ask the owner design-changing questions, one at a time.
3. Copy `.agents/mainframe-workflow/templates/proposal.md` into the proposals folder and fill it in.
4. Stop for owner review before any code.

## Scenarios

Scenarios are where a proposal earns its keep. Writing them finds defects that reading the prose does not.

- Write each as given / when / then, and name its test.
- Write from a caller's view, not from inside the implementation.
- A scenario that needs a later phase goes in that phase.
- Look, frame cost and allocation are not scenarios. Name the render test, benchmark or gate instead.
- Feel goes in the Human checklist.

## What to look for

Scenarios tend to expose three kinds of problem. Look for them while writing.

- What default arguments really do, as opposed to what the prose assumes.
- Things one phase uses that a later phase provides.
- Work that is named but defined nowhere.

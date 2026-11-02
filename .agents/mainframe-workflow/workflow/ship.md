# Ship

1. Run the gates from `.agents/workflow.md`.
2. Close the proposal, if there is one, as the Shipping section says:
   - `move`: its content moves to the current-state doc, with a "Changes from the proposal" section.
   - `status`: the doc stays, and gets a status and a dated build log.
3. Tick a milestone only when it works for a player, not when it compiles.
4. Write a log from `.agents/mainframe-workflow/templates/log.md`. Write none if nothing changed.
5. Update the state file by correcting it, not appending to it.
6. Run `.agents/mainframe-workflow/scripts/check.sh`.
7. Commit and push as the project's Commits and Git sections say.
8. Open the PR.
9. Once it merges, check its branch is gone from the remote. If it is not, delete it and turn on automatic deletion with `gh repo edit --delete-branch-on-merge`.

## PR body

- **What changed**
- **For review**: where a reviewer should look hardest.
- **Verification**: what ran, and what it showed.
- **Not done**: what was left out or not verified.
- **Human checks**: what a person must check.

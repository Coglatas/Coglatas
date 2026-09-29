# GitHub repository transfer: Coglatas organization to NYGsatoshi

This runbook covers transfer of GitHub repository ID `1261244608` from
`Coglatas/Coglatas` to `NYGsatoshi/Coglatas`.

## Invariants

- Repository ID `1261244608` is the stable repository identity.
- Required merge checks remain `build-test`, `frontend-test`,
  `security-scan`, and `publication-readiness`.
- Commit signatures and synthetic human-approval statuses are not merge gates.
- Active workflows must not depend on the current owner name when GitHub exposes
  `github.repository_id` or `GITHUB_REPOSITORY`.
- Future GHCR release image names intentionally follow `GITHUB_REPOSITORY`;
  after transfer they use the `nygsatoshi/coglatas` namespace.

## Before transfer

1. Merge the repository-transfer preparation PR while the repository is still
   `Coglatas/Coglatas`.
2. Confirm the four required checks pass on `main`.
3. Confirm active default-branch rulesets contain only the intended CI and PR
   protections.
4. Record the `syncfusion-licensed-build` Environment configuration and verify
   its secret remains configured.
5. Do not create a new repository at either the old or target location during
   the transfer.

## Transfer

Use GitHub repository settings to transfer `Coglatas/Coglatas` to the personal
account `NYGsatoshi`. Keep the repository name `Coglatas`.

## Immediately after transfer

1. Confirm the repository ID is still `1261244608`.
2. Confirm `main`, open pull requests, issues, releases, rulesets, Actions
   history, Environments, and repository secrets are present.
3. Confirm the active rulesets still require:
   - `build-test`
   - `frontend-test`
   - `security-scan`
   - `publication-readiness`
4. Run or observe a new `main` CI and Publication Readiness execution.
5. Run `Qodana Deep Analysis` on `main` and confirm the local reusable
   workflow resolves successfully.
6. Update local clones:
   `git remote set-url origin https://github.com/NYGsatoshi/Coglatas.git`.

## Follow-up cleanup

After the transfer is stable, remove `Coglatas/Coglatas` from
`repository_aliases` in the governance policy and regenerate
`governance/controls.md`.

Historical links do not need cosmetic rewriting solely because the owner changed
when GitHub's redirect is sufficient. Runtime workflow references and policy
identity checks must not rely on that redirect.

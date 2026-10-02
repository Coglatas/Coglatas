# Docker Development Environment

The contributor-facing development guide now lives in [README.dev-env.md](README.dev-env.md).

Use these modes:

- Recommended default: `infra/compose/dev/db.yml` for PostgreSQL only, with host `dotnet run` and host `frontend npm run start`
- Optional full Docker stack: `infra/compose/dev/full.yml`
- Optional Linux screenshot parity runner: `infra/compose/dev/playwright.yml` plus `infra/docker/playwright.Dockerfile`

Full application Docker is optional and should not be treated as the default contributor workflow.

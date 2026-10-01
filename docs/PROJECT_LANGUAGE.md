# Project language policy

Decision date: 2026-09-30  
Owner decision and migration tracker: [LANG-01 / #965](https://github.com/NYGsatoshi/Coglatas/issues/965)

## One canonical language

English is the sole canonical language for Coglatas engineering and project records.
Japanese document editions are not required. Do not create or maintain parallel
Japanese documents, bilingual engineering summaries, or Japanese-only companion
artifacts. A request or preliminary discussion in another language does not change
the language of the repository artifact produced from it.

This rule covers issue and pull-request titles/bodies, comments and reviews,
new commit messages, branch names, labels and milestone descriptions, project
metadata, release notes, README files, specifications, ADRs, active and archived
engineering documents, source comments/docstrings, test descriptions, configuration
documentation, and AI-generated repository output. Use established English domain
terms and existing stable identifiers rather than inventing synonyms.

Prefer clear, direct technical English. Keep complete acceptance criteria,
qualifications, negative requirements, and evidence. Do not shorten a record merely
to reduce its translation length or token count.

## Preserve semantics, not duplicate editions

Translate an existing record in place when authorized and safe. Keep its stable URL
and identity. Where multiple language editions exist, first reconcile unique content
into the canonical English document, repair inbound links, and only then retire a
redundant edition through a reviewed change. Do not remove unique requirements just
to eliminate a Japanese file.

An English translation is authoritative only as a faithful rendering of the adopted
source decision. Language conversion does not change the project's authority order,
turn proposals into approvals, revive superseded decisions, or make unimplemented
behavior complete. Preserve the original chronology and distinguish historical
records from current requirements. Resolve an ambiguous or conflicting meaning
explicitly; do not silently choose the translator's preferred interpretation.

## Narrow preservation exceptions

The following content may retain its original language; surrounding engineering
explanations must be English:

- Intentional end-user localization/i18n resources and locale-specific UI copy.
- Japanese IME, Unicode, search, accessibility, and encoding test fixtures whose
  original text is necessary to test the behavior.
- Exact diagnostic messages, logs, screenshots, and attributed quotations when
  changing them would corrupt evidence. Provide an English explanation or caption.
- Third-party/legal originals, proper names, external reference titles/URLs, and
  other immutable source material. Preserve attribution and legal meaning.
- Historical Git objects, signed commits/tags, and contracts whose identifiers or
  literal values cannot be changed safely as a documentation-only operation.

These are not exceptions for writing new Japanese engineering prose or maintaining
a Japanese documentation edition. Document the reason for each non-obvious
exception in English. Do not rewrite product UI, external contracts, migration
identifiers, lockfiles, generated assets, or test oracles solely to satisfy a
language scan. Do not use a blanket non-ASCII ban: mathematical symbols and valid
Unicode data are not language-policy violations.

## Issue, PR, and comment migration

1. Read the entire current title/body and relevant decision addenda before editing.
   Capture the original record for comparison in an authorized working context;
   do not expose private source material in public migration evidence.
2. Preserve all stable references, acceptance criteria and checkbox states,
   timestamps and version numbers, dependency directions, decision holds,
   security qualifications, and evidence limitations.
3. Replace only the language-bearing fields needed for translation. Do not change
   state, state reason, assignees, labels, milestones, review state, PR base/head,
   or merge settings as a side effect. A metadata label rename needs its own
   inventory of automation and query references before migration.
4. Preserve author identity and chronology. Translate repository-owner-authored or
   authorized automation-authored comments in place only when permissions and
   provenance allow. Identify translated quotations as translations. Do not make
   another person's statement appear newly authored by them; when an original must
   remain, link an explicitly labeled English rendering and record the exception.
5. Compare the replacement with the original, read back the saved result, and log
   the exact records completed in #965. Existing English records need no cosmetic
   rewrite. Preserve closed issues/PRs as closed; never merge a PR as a translation
   side effect.

An updated issue body does not prove that its comments, linked documents, or private
specifications were translated. Report those surfaces separately.

## Repository and AI workflow

Follow this policy together with `AGENTS.md`, `CONTRIBUTING.md`, and task-specific
security/architecture rules. Copilot and other agents must produce English issue/PR
text, reviews, progress comments posted to GitHub, and repository documentation,
even when the user's task request is in Japanese. Keep CodeRabbit's English output
setting and preserve its existing security, privacy, and review scope.

Before submitting a PR, check the title/body, changed engineering prose, comments,
and documentation links. Translate non-English engineering prose in the portion
being added or materially revised; track unrelated legacy material separately to
keep implementation changes reviewable. Use dedicated batches for the full legacy
migration. Do not invent functional changes while translating comments.

Do not weaken required checks or add privileged write automation for translation.
Templates and agent instructions guide contributors; they are not proof of an
exhaustive language audit or a machine-enforced language detector.

## Confidentiality and ownership

This publicly visible repository is not an open-source project. The language policy
does not change contribution authorization, licenses, or sharing permissions.
Do not copy private `AIPsiteNYGspec` requirements, school-internal information,
credentials, private URLs, or proprietary material into public issues or PRs.
Translate private artifacts only in their authorized private location. Reference
only stable sanitized requirement identifiers in public review records.

## Migration accounting and completion

Track open and closed issues/PRs, comments/review discussions, active and archived
documents, source/configuration prose, metadata, release notes, and any applicable
wiki/discussion surfaces separately in #965. Inventory pagination must be complete;
a search excerpt or first page is not an exhaustive count.

Record the inspected scope, translated records, records already in English,
preserved exceptions, inaccessible or unsupported surfaces, validation performed,
and remaining work. Do not infer completion from filename scans or character scans.
Do not close the migration tracker until all applicable surfaces are covered or
individually documented as approved preservation exceptions or unresolved blockers.

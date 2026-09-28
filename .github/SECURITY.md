# Security policy

>[!IMPORTANT]
>We will not be accepting reports related to Angular, JavaScript, TypeScript or something which is related to the current frontend
>due to deprecation.
>We will be migrating to Avalonia, C# and XAML based frontend. If you find any issues about frontend which is related to codes
>written with C# or xAML using .NET and Avalonia framework, we'll accept and review those. Anything other than that which will be
>deprecated will not be maintained.

## Private reporting only

Do not report suspected vulnerabilities through a public issue, pull request,
discussion, commit, or CI log.

After the repository is public, use GitHub private vulnerability reporting when
that feature is enabled. If private vulnerability reporting is unavailable,
contact the repository owner through a private contact method shown on the
owner's GitHub profile and disclose only the minimum information needed to
establish contact.

A useful private report includes:

- the affected commit or version;
- the affected component;
- reproducible, non-destructive steps;
- the expected and observed behavior; and
- a concise impact assessment with secrets and personal data redacted.

## Testing boundaries

Publication of source code does not authorize testing against any live,
school-operated, demonstration, staging, or production deployment. Do not:

- access or attempt to access another person's account or data;
- use real credentials, tokens, files, messages, or student information;
- perform denial-of-service, persistence, phishing, social engineering, or
  destructive testing; or
- publish exploit details before the repository owner has completed review.

Use only systems and synthetic data for which you have explicit permission.

## Supported scope

Security reports are evaluated against the current `main` branch and explicitly
identified supported deployments. Historical documents under `docs/archive/`
may describe obsolete behavior and are not independently supported.

There is no public bug-bounty program and no promise of compensation.

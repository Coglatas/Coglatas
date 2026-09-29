# Security policy

>[!IMPORTANT]
>The frontend is being migrated from Angular, JavaScript, and TypeScript to an Avalonia-based frontend using C# and XAML.
>Security reports for frontend code that is still present on the current `main` branch or in an explicitly supported deployment
>remain in scope until that code is removed or explicitly declared unsupported.
>Once legacy frontend code has been removed or declared unsupported, reports that apply only to that retired code may be closed
>as out of scope. Security reports affecting the supported Avalonia/.NET frontend remain fully in scope.

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

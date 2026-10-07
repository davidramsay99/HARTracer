# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

## What is signed

Only release builds of HARborer: `HARborer.exe` and the `HARborer-setup-*.exe` installers attached to
[GitHub Releases](https://github.com/davidramsay99/HARTracer/releases). They are built by the GitHub Actions workflow
in `.github/workflows/build.yml` from the source at a tagged commit in this public repository. Nothing built
elsewhere, and no third-party binary, is submitted for signing.

## Team roles

| Role | Members |
| :- | :- |
| Committers and reviewers | [davidramsay99](https://github.com/davidramsay99) |
| Approvers | [davidramsay99](https://github.com/davidramsay99) |

Every signing request is approved manually by an approver. All team members use multi-factor authentication on
GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the
user or the person installing or operating it. HARborer opens a network connection only when the user presses Send in
the Request Composer with Offline Mode turned off, and only to the host the user entered. It has no telemetry,
update checks or crash reporting.

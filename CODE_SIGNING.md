# Code signing policy

BK7231 GUI Flash Tool is preparing to use **free code signing provided by SignPath.io, certificate by SignPath Foundation** for official Windows release binaries.

## Scope

The project-owned binary covered by this policy is:

- `BK7231Flasher.exe`

Third-party runtime DLLs distributed with the application are upstream dependencies and are not signed with the BK7231 GUI Flash Tool project certificate.

## Build and release provenance

Official signed releases must:

- be built from the `openshwprojects/BK7231GUIFlashTool` repository;
- be built on GitHub-hosted Windows runners using the repository's GitHub Actions workflow;
- originate from the `main` branch;
- be submitted to SignPath through its GitHub trusted-build integration;
- pass SignPath origin verification;
- receive manual signing approval before a public release is created.

Normal pull-request and push builds may remain unsigned CI artifacts. They are not official signed releases.

## Project roles

- Project owner / committer / reviewer: [@openshwprojects](https://github.com/openshwprojects)
- Reviewer / signing approver: [@divadiow](https://github.com/divadiow)
- Signing approver: [@openshwprojects](https://github.com/openshwprojects)

Changes to the release workflow and this signing policy are treated as security-sensitive changes and should be reviewed by the project owner.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Network operations in the application, including downloading firmware from GitHub, scanning a selected LAN range, and communicating with selected OpenBeken devices, are initiated by explicit user actions.

## SignPath configuration

The intended SignPath project configuration signs only `BK7231Flasher.exe` with Authenticode SHA-256. The repository contains a reference artifact configuration at `.signpath/artifact-configuration.xml` for use when creating the SignPath project.

The GitHub Actions workflow expects these repository settings after SignPath Foundation approval:

- Secret: `SIGNPATH_API_TOKEN`
- Variable: `SIGNPATH_ORGANIZATION_ID`
- Variable: `SIGNPATH_PROJECT_SLUG`
- Variable: `SIGNPATH_SIGNING_POLICY_SLUG`
- Variable: `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`

## Eligibility note

SignPath Foundation requires an OSI-approved open-source licence. The repository must have an explicit project licence before the SignPath Foundation application can be completed.
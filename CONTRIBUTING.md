# Contributing to Piper

Piper accepts focused pull requests with tests. Because it processes untrusted network traffic and
can install a local certificate authority, security and failure behavior are part of every change.

## Before opening a pull request

1. Read [CLAUDE.md](CLAUDE.md), including the protocol and certificate safety rules.
2. Add a regression or behavior test under `tests/Piper.SmokeTests`, or under `tests/Piper.UiTests`
   when the behavior lives in a native WinForms control. The UI tests open windows, so they need
   an interactive desktop session; where there is none, pass `-SkipUiTests` to the gate below and
   let CI run them. CI runs them at 96 DPI. At 150% display scaling one grid test, "hiding a host
   from a grid scrolled a few rows shows the rest from the top", currently fails on `main`; it is a
   known layout issue in the session grid, so say so in your pull request rather than hiding it.
3. Run the same deterministic gate as CI:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify.ps1
   ```

   Changes under `installer/` must also be verified with `-IncludeInstaller` after installing NSIS.
   CI always performs the installer build.

4. Complete the pull-request template. Explain security impact and manual verification rather than
   checking boxes without evidence.

Warnings are errors in every build (`Directory.Build.props`), and `global.json` requires a .NET 10 SDK
(10.0.103 or a later 10.0 release). CI runs the gate above, so it audits NuGet packages, runs
analyzer diagnostics and the full smoke and UI suites, reviews new
dependencies, and performs CodeQL analysis. A maintainer can request the separate Claude review by
applying the `claude-review` label after the normal checks pass; it checks the diff against the
repository rules and blocks on actionable defects.

Maintainers should require the `CI` and `Security` checks. A `Claude review` check runs only after a
maintainer applies its label, so do not configure it as a repository-wide required status check;
when requested, it must pass before merge. Changes to security-sensitive code or the review
automation still deserve human judgment even when all automation is green.

## What changed

Describe the behavior change and why this is the smallest coherent fix.

## Risk and security

- What untrusted input or privilege boundary does this touch?
- Could it affect certificate trust, proxy settings, captured secrets, protocol framing, resource
  limits, cancellation, persistence, installation, or release behavior?
- What is the failure and rollback behavior?

Write `None` only after considering the questions above.

## Verification

- [ ] I added or updated tests for material behavior and regressions.
- [ ] I ran `powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify.ps1`.
- [ ] For installer changes, I also ran the gate with `-IncludeInstaller` and NSIS installed.
- [ ] I updated user-facing documentation where behavior or security assumptions changed.
- [ ] I did not include credentials, cookies, captured traffic, certificates, private keys, or other
      third-party data.

List any manual verification and explain any test exception:

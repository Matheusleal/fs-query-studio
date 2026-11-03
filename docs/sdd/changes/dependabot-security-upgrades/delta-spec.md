# Delta Spec: Dependabot Security Upgrades and CI Stabilization

## MODIFIED Requirements

### REQ-SEC-01: Secure and Compatible Node.js Development Dependencies
Where development dependencies are installed in `editors/vscode`,
when `npm ci` is executed,
the package manager shall resolve all dependencies without `ERESOLVE` peer dependency conflicts against Node.js 20 and `@types/node@^20.14.0`.

### REQ-SEC-02: Mitigation of Known Vulnerabilities
Where `editors/vscode` dependencies are resolved,
the package tree shall update `brace-expansion` and `esbuild` to versions containing security patches for known CVEs (CVE-2026-102277, CVE-2025-02-10/GHSA-67mh-4wv8-2f99).

### REQ-SEC-03: Full Solution Test and Build Stability
Where the solution is built and tested across Windows and Linux,
the test execution engine shall complete all unit, integration, and sample notebook test runs with 100% success and no hangs.

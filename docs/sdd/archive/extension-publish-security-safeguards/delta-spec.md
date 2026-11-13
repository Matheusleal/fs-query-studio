# Delta Spec: Extension Packaging and Publish Security Safeguards

## ADDED Requirements (GEARS Syntax)

### SEC-PKG-01: Exclusion of Debug Symbols and Source Files
```text
When packaging the extension into a VSIX archive,
the packager shall exclude all debug symbol files matching '*.pdb',
all source map files matching '*.map',
and all source code files matching '*.fs', '*.cs', '*.ts', and '*.tsx',
retaining only compiled runtime distribution bundles.
```

### SEC-PKG-02: Exclusion of Secrets, Environment Files, and Private Keys
```text
When packaging the extension or scanning package manifests,
the packaging pipeline shall exclude all environment files matching '.env*',
all private key and certificate files matching '*.key', '*.pem', '*.cert', '*.pfx', '*.p12',
and all credential or token configuration files matching '*secret*' or '*token*'.
```

### SEC-PKG-03: Exclusion of Notebooks, Local Databases, and Nested Packages
```text
When packaging the extension,
the packager shall exclude all local notebook files matching '*.fsqnb',
all database storage files matching '*.db', '*.sqlite', '*.sqlite3', '*.bak', '*.dump',
and all archive packages matching '*.vsix'.
```

### SEC-PKG-04: Strict VSIX Contents Verification Gate
```text
When the packaging assertion function executes on the packaged files listing,
the verification gate shall inspect all file entries against the forbidden security patterns,
and shall fail with an error listing every offending file if any debug symbol, source file, secret, local database, or nested package is detected.
```

### SEC-PKG-05: Preflight Security Audit
```text
When 'npm run doctor' or 'npm run publish' is invoked,
the security audit check shall scan the extension directory for untracked or unignored sensitive files,
and shall report a hard blocker if any secret, private key, or unauthorized unbundled source artifact is present.
```

### SEC-PKG-06: Pre-commit Hook Secret Protection
```text
When a git commit is attempted,
the pre-commit hook shall scan staged changes for known secret formats and credential patterns,
and shall reject the commit if any pattern matches an exposed private key, secret environment file, or credential.
```

## MODIFIED Requirements (GEARS Syntax)

### MOD-PKG-01: Worker Build Cleansing
```text
When the worker build process completes 'dotnet publish' for Release configuration,
the build runner shall strip all debug symbol files (*.pdb) and documentation xml files (*.xml) from 'editors/vscode/dist/worker',
ensuring that only runtime executable binaries and dependencies are preserved for packaging.
```

import { describe, it, expect } from "vitest";
// @ts-ignore
import { assertVsixContents, forbiddenVsixRules } from "../scripts/lib.mjs";
// @ts-ignore
import { scanSensitiveFiles } from "../scripts/doctor.mjs";
import * as fs from "node:fs";
import * as path from "node:path";
import * as os from "node:os";

const validBaseListing = [
  "package.json",
  "README.md",
  "LICENSE",
  "CHANGELOG.md",
  "icon.png",
  "dist/extension.js",
  "dist/worker/FSharpQueryStudio.Worker.dll",
  "dist/worker/FSharpQueryStudio.Worker.deps.json",
  "dist/worker/FSharpQueryStudio.Worker.runtimeconfig.json",
  "media/renderer/index.js",
  "media/activitybar.svg",
  "media/icon.png",
  "syntaxes/fsharp-query.tmLanguage.json",
].join("\n");

describe("Packaging Security Safeguards", () => {
  it("passes for clean, valid distribution package listing", () => {
    expect(() => assertVsixContents(validBaseListing)).not.toThrow();
  });

  it("fails if any required distribution file is missing", () => {
    const missingWorkerDll = validBaseListing
      .split("\n")
      .filter((l) => !l.includes("FSharpQueryStudio.Worker.dll"))
      .join("\n");
    expect(() => assertVsixContents(missingWorkerDll)).toThrow(/missing required file/);
  });

  describe("Leak prevention assertions in assertVsixContents", () => {
    it("rejects debug symbols and source maps (.pdb, .map)", () => {
      const listingWithPdb = `${validBaseListing}\ndist/worker/FSharpQueryStudio.Worker.pdb`;
      expect(() => assertVsixContents(listingWithPdb)).toThrow(/debug symbols & source maps/);

      const listingWithMap = `${validBaseListing}\ndist/extension.js.map`;
      expect(() => assertVsixContents(listingWithMap)).toThrow(/debug symbols & source maps/);
    });

    it("rejects uncompiled source code files (.fs, .cs, .ts, loose .js)", () => {
      const withFs = `${validBaseListing}\ndist/worker/Connector.fs`;
      expect(() => assertVsixContents(withFs)).toThrow(/uncompiled source code files/);

      const withCs = `${validBaseListing}\nNativeBridge.cs`;
      expect(() => assertVsixContents(withCs)).toThrow(/uncompiled source code files/);

      const withTs = `${validBaseListing}\nhelper.ts`;
      expect(() => assertVsixContents(withTs)).toThrow(/uncompiled source code files/);

      const withLooseJs = `${validBaseListing}\nextra.js`;
      expect(() => assertVsixContents(withLooseJs)).toThrow(/uncompiled source code files/);
    });

    it("rejects secrets, environment configs and private keys", () => {
      const withDotEnv = `${validBaseListing}\n.env`;
      expect(() => assertVsixContents(withDotEnv)).toThrow(/secrets, environment configs & private keys/);

      const withDotEnvLocal = `${validBaseListing}\n.env.production`;
      expect(() => assertVsixContents(withDotEnvLocal)).toThrow(/secrets, environment configs & private keys/);

      const withKey = `${validBaseListing}\nserver.key`;
      expect(() => assertVsixContents(withKey)).toThrow(/secrets, environment configs & private keys/);

      const withPem = `${validBaseListing}\ncert.pem`;
      expect(() => assertVsixContents(withPem)).toThrow(/secrets, environment configs & private keys/);

      const withPfx = `${validBaseListing}\ncert.pfx`;
      expect(() => assertVsixContents(withPfx)).toThrow(/secrets, environment configs & private keys/);

      const withSecretsJson = `${validBaseListing}\nsecrets.json`;
      expect(() => assertVsixContents(withSecretsJson)).toThrow(/secrets, environment configs & private keys/);
    });

    it("rejects local databases, notebooks and data dumps", () => {
      const withDb = `${validBaseListing}\nlocal.db`;
      expect(() => assertVsixContents(withDb)).toThrow(/local databases, notebooks & data dumps/);

      const withSqlite = `${validBaseListing}\ncache.sqlite3`;
      expect(() => assertVsixContents(withSqlite)).toThrow(/local databases, notebooks & data dumps/);

      const withNotebook = `${validBaseListing}\nnotebook.fsqnb`;
      expect(() => assertVsixContents(withNotebook)).toThrow(/local databases, notebooks & data dumps/);

      const withDump = `${validBaseListing}\nbackup.dump`;
      expect(() => assertVsixContents(withDump)).toThrow(/local databases, notebooks & data dumps/);
    });

    it("rejects source, test and build directories (src, test, out, node_modules)", () => {
      const withSrc = `${validBaseListing}\nsrc/extension.ts`;
      expect(() => assertVsixContents(withSrc)).toThrow(/source, test & build directories/);

      const withTest = `${validBaseListing}\ntest/unit.test.ts`;
      expect(() => assertVsixContents(withTest)).toThrow(/source, test & build directories/);

      const withNodeModules = `${validBaseListing}\nnode_modules/foo/index.js`;
      expect(() => assertVsixContents(withNodeModules)).toThrow(/source, test & build directories/);
    });

    it("rejects VCS, IDE and agent metadata", () => {
      const withGit = `${validBaseListing}\n.git/config`;
      expect(() => assertVsixContents(withGit)).toThrow(/vcs, ide & agent metadata/);

      const withGithub = `${validBaseListing}\n.github/workflows/ci.yml`;
      expect(() => assertVsixContents(withGithub)).toThrow(/vcs, ide & agent metadata/);

      const withAgents = `${validBaseListing}\n.agents/rules.md`;
      expect(() => assertVsixContents(withAgents)).toThrow(/vcs, ide & agent metadata/);
    });

    it("rejects nested package archives (.vsix)", () => {
      const withVsix = `${validBaseListing}\nfs-query-studio.vsix`;
      expect(() => assertVsixContents(withVsix)).toThrow(/nested package archives/);
    });
  });

  describe("Doctor preflight scanSensitiveFiles", () => {
    it("detects sensitive files and reports them accurately", () => {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "fsqs-sec-test-"));
      try {
        fs.writeFileSync(path.join(tmpDir, ".env"), "API_KEY=123");
        fs.writeFileSync(path.join(tmpDir, "prod.env.local"), "SECRET=456");
        fs.writeFileSync(path.join(tmpDir, "server.key"), "KEY");
        fs.writeFileSync(path.join(tmpDir, "cert.pem"), "PEM");
        fs.writeFileSync(path.join(tmpDir, "secrets.json"), "{}");
        fs.writeFileSync(path.join(tmpDir, "data.sqlite"), "DATA");
        fs.writeFileSync(path.join(tmpDir, "clean.txt"), "OK");

        const found = scanSensitiveFiles(tmpDir);
        expect(found).toContain(".env");
        expect(found).toContain("prod.env.local");
        expect(found).toContain("server.key");
        expect(found).toContain("cert.pem");
        expect(found).toContain("secrets.json");
        expect(found).toContain("data.sqlite");
        expect(found).not.toContain("clean.txt");
      } finally {
        fs.rmSync(tmpDir, { recursive: true, force: true });
      }
    });
  });
});

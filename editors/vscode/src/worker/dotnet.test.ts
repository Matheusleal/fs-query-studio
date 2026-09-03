import { describe, it, expect } from "vitest";
import { resolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./dotnet";

const ok = (version: string) => async () => ({ stdout: version, code: 0 });

describe("resolveDotnet", () => {
  it("resolves when dotnet 10 is present", async () => {
    const info = await resolveDotnet({ run: ok("10.0.100\n"), env: {} });
    expect(info).toEqual({ command: "dotnet", version: "10.0.100" });
  });

  it("throws DotnetTooOldError for dotnet 8", async () => {
    await expect(resolveDotnet({ run: ok("8.0.404"), env: {} })).rejects.toBeInstanceOf(
      DotnetTooOldError,
    );
  });

  it("DotnetTooOldError carries the found version and a user message", async () => {
    const err = await resolveDotnet({ run: ok("8.0.404"), env: {} }).catch((e) => e);
    expect(err.foundVersion).toBe("8.0.404");
    expect(err.userMessage).toMatch(/10/);
  });

  it("throws DotnetNotFoundError when the process cannot start", async () => {
    const run = async () => {
      throw new Error("ENOENT");
    };
    await expect(resolveDotnet({ run, env: {} })).rejects.toBeInstanceOf(DotnetNotFoundError);
  });

  it("throws DotnetNotFoundError when --version exits non-zero", async () => {
    const run = async () => ({ stdout: "", code: 1 });
    await expect(resolveDotnet({ run, env: {} })).rejects.toBeInstanceOf(DotnetNotFoundError);
  });
});

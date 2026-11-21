import { describe, it, expect, beforeEach } from "vitest";
import * as vscodeStub from "../../test/vscode-stub";
import { BackgroundOrchestrator, type BackgroundOrchestratorDeps } from "./background-orchestrator";

describe("BackgroundOrchestrator", () => {
  beforeEach(() => {
    vscodeStub.commands._handlers.clear();
  });

  it("initializes context to false and registers all pause/resume/toggle command variants", () => {
    const logs: string[] = [];
    const deps: BackgroundOrchestratorDeps = {
      log: (l) => logs.push(l),
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    expect(orchestrator.isPaused()).toBe(false);

    expect(vscodeStub.commands._handlers.has("fs-query-studio.pauseWatchersAndWorkers")).toBe(true);
    expect(vscodeStub.commands._handlers.has("fs-query-studio.pauseBackground")).toBe(true);
    expect(vscodeStub.commands._handlers.has("fs-query-studio.resumeWatchersAndWorkers")).toBe(true);
    expect(vscodeStub.commands._handlers.has("fs-query-studio.resumeBackground")).toBe(true);
    expect(vscodeStub.commands._handlers.has("fs-query-studio.togglePauseResume")).toBe(true);
    expect(vscodeStub.commands._handlers.has("fs-query-studio.toggleBackground")).toBe(true);

    orchestrator.dispose();
  });

  it("pauses background operations, updates status bar and invokes onPause", async () => {
    let pausedTriggered = false;
    const logs: string[] = [];
    const statusBar: any = {
      text: "",
      tooltip: "",
      command: "",
      show: () => {},
    };

    const deps: BackgroundOrchestratorDeps = {
      log: (l) => logs.push(l),
      statusBar,
      onPause: async () => {
        pausedTriggered = true;
      },
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    await orchestrator.pause(true);

    expect(orchestrator.isPaused()).toBe(true);
    expect(pausedTriggered).toBe(true);
    expect(statusBar.text).toBe("$(play) F# Query: Paused");
    expect(statusBar.command).toBe("fs-query-studio.resumeWatchersAndWorkers");
    expect(logs.some((l) => l.includes("Watchers and workers paused"))).toBe(true);

    // Idempotency: second pause call does not re-invoke
    pausedTriggered = false;
    await orchestrator.pause(true);
    expect(pausedTriggered).toBe(false);

    orchestrator.dispose();
  });

  it("pauses background operations cleanly without statusBar and without onPause callback", async () => {
    const logs: string[] = [];
    const deps: BackgroundOrchestratorDeps = {
      log: (l) => logs.push(l),
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    await orchestrator.pause(true);
    expect(orchestrator.isPaused()).toBe(true);
    expect(logs.some((l) => l.includes("Watchers and workers paused"))).toBe(true);

    orchestrator.dispose();
  });

  it("resumes background operations, restores status bar and invokes onResume", async () => {
    let resumedTriggered = false;
    const logs: string[] = [];
    const statusBar: any = {
      text: "",
      tooltip: "",
      command: "",
      show: () => {},
    };

    const deps: BackgroundOrchestratorDeps = {
      log: (l) => logs.push(l),
      statusBar,
      onResume: async () => {
        resumedTriggered = true;
      },
    };

    const orchestrator = new BackgroundOrchestrator(deps);

    // Calling resume when not paused is a no-op
    await orchestrator.resume(true);
    expect(resumedTriggered).toBe(false);

    // Pause first
    await orchestrator.pause(true);
    expect(orchestrator.isPaused()).toBe(true);

    // Now resume
    await orchestrator.resume(true);
    expect(orchestrator.isPaused()).toBe(false);
    expect(resumedTriggered).toBe(true);
    expect(statusBar.text).toBe("$(database) F# Query Studio");
    expect(statusBar.command).toBe("fs-query-studio.showOutput");
    expect(logs.some((l) => l.includes("Watchers and workers resumed"))).toBe(true);

    orchestrator.dispose();
  });

  it("resumes background operations cleanly without statusBar and without onResume callback", async () => {
    const logs: string[] = [];
    const deps: BackgroundOrchestratorDeps = {
      log: (l) => logs.push(l),
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    await orchestrator.pause(true);
    expect(orchestrator.isPaused()).toBe(true);

    await orchestrator.resume(true);
    expect(orchestrator.isPaused()).toBe(false);
    expect(logs.some((l) => l.includes("Watchers and workers resumed"))).toBe(true);

    orchestrator.dispose();
  });

  it("toggles pause and resume states back and forth", async () => {
    let pauseCount = 0;
    let resumeCount = 0;
    const deps: BackgroundOrchestratorDeps = {
      log: () => {},
      onPause: () => {
        pauseCount++;
      },
      onResume: () => {
        resumeCount++;
      },
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    expect(orchestrator.isPaused()).toBe(false);

    await orchestrator.toggle();
    expect(orchestrator.isPaused()).toBe(true);
    expect(pauseCount).toBe(1);

    await orchestrator.toggle();
    expect(orchestrator.isPaused()).toBe(false);
    expect(resumeCount).toBe(1);

    orchestrator.dispose();
  });

  it("executes registered commands via vscode.commands.executeCommand", async () => {
    const deps: BackgroundOrchestratorDeps = {
      log: () => {},
    };

    const orchestrator = new BackgroundOrchestrator(deps);

    // Test pauseBackground command
    await vscodeStub.commands.executeCommand("fs-query-studio.pauseBackground");
    expect(orchestrator.isPaused()).toBe(true);

    // Test resumeBackground command
    await vscodeStub.commands.executeCommand("fs-query-studio.resumeBackground");
    expect(orchestrator.isPaused()).toBe(false);

    // Test toggleBackground command
    await vscodeStub.commands.executeCommand("fs-query-studio.toggleBackground");
    expect(orchestrator.isPaused()).toBe(true);

    // Test togglePauseResume command
    await vscodeStub.commands.executeCommand("fs-query-studio.togglePauseResume");
    expect(orchestrator.isPaused()).toBe(false);

    // Test pauseWatchersAndWorkers command
    await vscodeStub.commands.executeCommand("fs-query-studio.pauseWatchersAndWorkers");
    expect(orchestrator.isPaused()).toBe(true);

    // Test resumeWatchersAndWorkers command
    await vscodeStub.commands.executeCommand("fs-query-studio.resumeWatchersAndWorkers");
    expect(orchestrator.isPaused()).toBe(false);

    orchestrator.dispose();
  });

  it("shows information message on pause and resume when silent is false", async () => {
    const messages: string[] = [];
    const origShowInfo = vscodeStub.window.showInformationMessage;
    (vscodeStub.window as any).showInformationMessage = async (msg: string) => {
      messages.push(msg);
    };

    const deps: BackgroundOrchestratorDeps = {
      log: () => {},
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    try {
      await orchestrator.pause(false);
      expect(messages.some((m) => m.includes("Watchers and workers paused"))).toBe(true);

      await orchestrator.resume(false);
      expect(messages.some((m) => m.includes("Watchers and workers resumed"))).toBe(true);
    } finally {
      (vscodeStub.window as any).showInformationMessage = origShowInfo;
      orchestrator.dispose();
    }
  });

  it("disposes command handlers on dispose()", () => {
    const deps: BackgroundOrchestratorDeps = {
      log: () => {},
    };

    const orchestrator = new BackgroundOrchestrator(deps);
    expect(orchestrator.getDisposables().length).toBe(6);

    orchestrator.dispose();
    expect(orchestrator.getDisposables().length).toBe(0);
  });
});

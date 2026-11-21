import * as vscode from "vscode";

export interface BackgroundOrchestratorDeps {
  log: (line: string) => void;
  statusBar?: vscode.StatusBarItem;
  onPause?: () => void | Promise<void>;
  onResume?: () => void | Promise<void>;
}

export class BackgroundOrchestrator implements vscode.Disposable {
  private _isPaused = false;
  private readonly disposables: vscode.Disposable[] = [];

  constructor(private readonly deps: BackgroundOrchestratorDeps) {
    void vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", false);

    const pauseCmd = vscode.commands.registerCommand(
      "fs-query-studio.pauseWatchersAndWorkers",
      () => this.pause(),
    );
    const pauseBgCmd = vscode.commands.registerCommand(
      "fs-query-studio.pauseBackground",
      () => this.pause(),
    );
    const resumeCmd = vscode.commands.registerCommand(
      "fs-query-studio.resumeWatchersAndWorkers",
      () => this.resume(),
    );
    const resumeBgCmd = vscode.commands.registerCommand(
      "fs-query-studio.resumeBackground",
      () => this.resume(),
    );
    const toggleCmd = vscode.commands.registerCommand(
      "fs-query-studio.togglePauseResume",
      () => this.toggle(),
    );
    const toggleBgCmd = vscode.commands.registerCommand(
      "fs-query-studio.toggleBackground",
      () => this.toggle(),
    );

    this.disposables.push(
      pauseCmd,
      pauseBgCmd,
      resumeCmd,
      resumeBgCmd,
      toggleCmd,
      toggleBgCmd,
    );
  }

  public isPaused(): boolean {
    return this._isPaused;
  }

  public async pause(silent = false): Promise<void> {
    if (this._isPaused) return;
    this._isPaused = true;
    await vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", true);

    if (this.deps.onPause) {
      await this.deps.onPause();
    }

    this.deps.log("[Host] Watchers and workers paused; worker session disposed and RAM/CPU released.");

    const statusBar = this.deps.statusBar;
    if (statusBar) {
      statusBar.text = "$(play) F# Query: Paused";
      statusBar.tooltip = "F# Query Studio: Watchers and workers paused (RAM and CPU released). Click to resume.";
      statusBar.command = "fs-query-studio.resumeWatchersAndWorkers";
      statusBar.show();
    }

    if (!silent) {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const msg = isPt
        ? "F# Query Studio: Watchers e workers pausados. O processo em background foi encerrado e a memória liberada."
        : "F# Query Studio: Watchers and workers paused. Background process terminated and memory released.";
      void vscode.window.showInformationMessage(msg);
    }
  }

  public async resume(silent = false): Promise<void> {
    if (!this._isPaused) return;
    this._isPaused = false;
    await vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", false);

    const statusBar = this.deps.statusBar;
    if (statusBar) {
      statusBar.text = "$(database) F# Query Studio";
      statusBar.tooltip = "F# Query Studio: Show Output Logs";
      statusBar.command = "fs-query-studio.showOutput";
      statusBar.show();
    }

    this.deps.log("[Host] Watchers and workers resumed.");

    if (this.deps.onResume) {
      await this.deps.onResume();
    }

    if (!silent) {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const msg = isPt
        ? "F# Query Studio: Watchers e workers retomados com sucesso."
        : "F# Query Studio: Watchers and workers resumed successfully.";
      void vscode.window.showInformationMessage(msg);
    }
  }

  public async toggle(): Promise<void> {
    if (this._isPaused) {
      await this.resume();
    } else {
      await this.pause();
    }
  }

  public getDisposables(): vscode.Disposable[] {
    return this.disposables;
  }

  public dispose(): void {
    for (const d of this.disposables) {
      d.dispose();
    }
    this.disposables.length = 0;
  }
}

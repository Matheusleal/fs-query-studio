import { describe, it, expect, vi, beforeEach } from "vitest";
import * as vscode from "vscode";
import {
  ConnectionHealthService,
  type ConnectionStatus,
  type StatusChangeEvent,
  type NotificationEvent,
} from "./health";

describe("ConnectionHealthService", () => {
  beforeEach(() => {
    (vscode.env as { language?: string }).language = "en";
  });

  // --- Scenario 1: Status Transitions ---
  describe("Scenario 1: Status Transitions", () => {
    it("1.1: defaults to connecting for unrecorded connection and fires onStatusChanged on update", () => {
      const service = new ConnectionHealthService();
      const events: StatusChangeEvent[] = [];
      service.onStatusChanged((e) => events.push(e));

      expect(service.getConnectionStatus("conn-1")).toBe("connecting");

      service.setConnectionStatus("conn-1", "connected");
      expect(service.getConnectionStatus("conn-1")).toBe("connected");
      expect(events).toEqual([{ id: "conn-1", status: "connected" }]);

      service.dispose();
    });

    it("1.2: transitions through connecting, connected, and disconnected firing events each time", () => {
      const service = new ConnectionHealthService();
      const events: StatusChangeEvent[] = [];
      service.onStatusChanged((e) => events.push(e));

      service.setConnectionStatus("conn-2", "connecting");
      service.setConnectionStatus("conn-2", "connected");
      service.setConnectionStatus("conn-2", "disconnected");

      expect(service.getConnectionStatus("conn-2")).toBe("disconnected");
      expect(events.map((e) => e.status)).toEqual(["connecting", "connected", "disconnected"]);

      service.dispose();
    });
  });

  // --- Scenario 2: Retry with Backoff ---
  describe("Scenario 2: Retry with Backoff", () => {
    it("2.1: computes exponential backoff delay correctly across multiple attempts and caps at maxDelayMs", () => {
      const service = new ConnectionHealthService({
        retryDelayMs: 100,
        backoffFactor: 2,
        maxDelayMs: 500,
      });

      // attempt 1: 100 * 2^0 = 100
      expect(service.calculateDelay(1)).toBe(100);
      // attempt 2: 100 * 2^1 = 200
      expect(service.calculateDelay(2)).toBe(200);
      // attempt 3: 100 * 2^2 = 400
      expect(service.calculateDelay(3)).toBe(400);
      // attempt 4: 100 * 2^3 = 800 -> capped at 500
      expect(service.calculateDelay(4)).toBe(500);

      service.dispose();
    });

    it("2.2: computes fixed delay when backoffFactor is omitted or set to 1", () => {
      const service = new ConnectionHealthService({ retryDelayMs: 250 });

      expect(service.calculateDelay(1)).toBe(250);
      expect(service.calculateDelay(2)).toBe(250);
      expect(service.calculateDelay(5)).toBe(250);

      service.dispose();
    });

    it("2.3: retries failed check with backoff and marks disconnected with error notification when exhausted", async () => {
      const notifications: NotificationEvent[] = [];
      const service = new ConnectionHealthService({
        retryDelayMs: 10,
        backoffFactor: 1,
      });
      service.onNotify((n) => notifications.push(n));

      let attempts = 0;
      const result = await service.checkWithRetry({
        connId: "conn-fail",
        connName: "FailingDB",
        maxRetries: 2,
        checkFn: async () => {
          attempts++;
          return undefined; // fails
        },
        getErrorMessage: () => "Port closed",
      });

      expect(result).toBeUndefined();
      expect(attempts).toBe(1);
      expect(service.getRetryCount("conn-fail")).toBe(1);
      expect(service.hasActiveTimer("conn-fail")).toBe(true);

      // Wait for attempt 2 and final exhaustion
      await new Promise((resolve) => setTimeout(resolve, 80));

      expect(attempts).toBe(3); // 1 initial + 2 retries
      expect(service.getConnectionStatus("conn-fail")).toBe("disconnected");
      expect(service.hasActiveTimer("conn-fail")).toBe(false);

      const warnings = notifications.filter((n) => n.type === "warning");
      const errors = notifications.filter((n) => n.type === "error");

      expect(warnings).toHaveLength(2);
      expect(warnings[0].message).toContain("attempt 1 of 2");
      expect(warnings[1].message).toContain("attempt 2 of 2");
      expect(errors).toHaveLength(1);
      expect(errors[0].message).toContain("after 2 attempts");

      service.dispose();
    });

    it("2.4: recovers on subsequent retry attempt and emits recovery info notification", async () => {
      const notifications: NotificationEvent[] = [];
      const service = new ConnectionHealthService({ retryDelayMs: 10 });
      service.onNotify((n) => notifications.push(n));

      let callCount = 0;
      await service.checkWithRetry({
        connId: "conn-recover",
        connName: "RecoveringDB",
        maxRetries: 3,
        checkFn: async () => {
          callCount++;
          if (callCount === 1) {
            return undefined;
          }
          return ["table1", "table2"];
        },
      });

      expect(callCount).toBe(1);
      expect(service.getConnectionStatus("conn-recover")).toBe("connecting");

      await new Promise((resolve) => setTimeout(resolve, 50));

      expect(callCount).toBe(2);
      expect(service.getConnectionStatus("conn-recover")).toBe("connected");
      expect(service.getRetryCount("conn-recover")).toBe(0);

      const infoNotifs = notifications.filter((n) => n.type === "info");
      expect(infoNotifs).toHaveLength(1);
      expect(infoNotifs[0].message).toContain("established successfully");

      service.dispose();
    });
  });

  // --- Scenario 3: Explicit Cancellation via cancelRetries ---
  describe("Scenario 3: Explicit Cancellation via cancelRetries", () => {
    it("3.1: cancelRetries clears pending timer and resets retry counts", async () => {
      const service = new ConnectionHealthService({ retryDelayMs: 50 });

      let attempts = 0;
      await service.checkWithRetry({
        connId: "conn-cancel",
        connName: "CancellableDB",
        maxRetries: 3,
        checkFn: async () => {
          attempts++;
          return undefined;
        },
      });

      expect(attempts).toBe(1);
      expect(service.hasActiveTimer("conn-cancel")).toBe(true);
      expect(service.getRetryCount("conn-cancel")).toBe(1);

      service.cancelRetries("conn-cancel");

      expect(service.hasActiveTimer("conn-cancel")).toBe(false);
      expect(service.getRetryCount("conn-cancel")).toBe(0);

      // Wait beyond the timer duration to ensure it does not fire
      await new Promise((resolve) => setTimeout(resolve, 100));
      expect(attempts).toBe(1);

      service.dispose();
    });

    it("3.2: cancelRetries invalidates in-flight checkFn operation preventing state corruption", async () => {
      const service = new ConnectionHealthService({ retryDelayMs: 10 });
      let finishCheck: (val: string[] | undefined) => void = () => {};

      const checkPromise = service.checkWithRetry({
        connId: "conn-slow",
        connName: "SlowDB",
        maxRetries: 2,
        checkFn: () =>
          new Promise<string[] | undefined>((resolve) => {
            finishCheck = resolve;
          }),
      });

      expect(service.getConnectionStatus("conn-slow")).toBe("connecting");

      // While in-flight, user deletes/cancels connection
      service.cancelRetries("conn-slow");
      service.setConnectionStatus("conn-slow", "disconnected");

      // Now resolve the slow checkFn with success
      finishCheck(["slow_table"]);
      await checkPromise;

      // Status should remain disconnected, not overwritten by delayed success
      expect(service.getConnectionStatus("conn-slow")).toBe("disconnected");

      service.dispose();
    });
  });

  // --- Scenario 4: Cleanup on clear and dispose ---
  describe("Scenario 4: Cleanup on clear and dispose", () => {
    it("4.1: clear cancels all timers and resets status and counts for all connections", async () => {
      const service = new ConnectionHealthService({ retryDelayMs: 100 });

      await service.checkWithRetry({
        connId: "c1",
        connName: "DB1",
        maxRetries: 2,
        checkFn: async () => undefined,
      });
      await service.checkWithRetry({
        connId: "c2",
        connName: "DB2",
        maxRetries: 2,
        checkFn: async () => undefined,
      });

      expect(service.hasActiveTimer("c1")).toBe(true);
      expect(service.hasActiveTimer("c2")).toBe(true);

      service.clear();

      expect(service.hasActiveTimer("c1")).toBe(false);
      expect(service.hasActiveTimer("c2")).toBe(false);
      expect(service.getRetryCount("c1")).toBe(0);
      expect(service.getRetryCount("c2")).toBe(0);
      expect(service.getConnectionStatus("c1")).toBe("connecting"); // default when reset

      service.dispose();
    });

    it("4.2: dispose clears timers and disposes event emitters safely", () => {
      const service = new ConnectionHealthService();
      let statusFired = 0;
      service.onStatusChanged(() => statusFired++);

      service.setConnectionStatus("c1", "connected");
      expect(statusFired).toBe(1);

      service.dispose();

      // After dispose, emitters should not throw and are disposed
      service.setConnectionStatus("c1", "disconnected");
      expect(service.hasActiveTimer("c1")).toBe(false);
    });
  });

  // --- Scenario 5: Localization ---
  describe("Scenario 5: Localization (Portuguese & English)", () => {
    it("5.1: emits notifications in Portuguese when isPortuguese is true", async () => {
      const notifications: NotificationEvent[] = [];
      const service = new ConnectionHealthService({
        retryDelayMs: 10,
        isPortuguese: () => true,
      });
      service.onNotify((n) => notifications.push(n));

      await service.checkWithRetry({
        connId: "pt-db",
        connName: "BancoPT",
        maxRetries: 1,
        checkFn: async () => undefined,
        getErrorMessage: () => "Falha de rede",
      });

      await new Promise((resolve) => setTimeout(resolve, 50));

      const warnings = notifications.filter((n) => n.type === "warning");
      const errors = notifications.filter((n) => n.type === "error");

      expect(warnings[0].message).toContain("não foi estabelecida");
      expect(warnings[0].message).toContain("tentativa 1 de 1");
      expect(errors[0].message).toContain("após 1 tentativas");

      service.dispose();
    });

    it("5.2: emits notifications in English when isPortuguese is false", async () => {
      const notifications: NotificationEvent[] = [];
      const service = new ConnectionHealthService({
        retryDelayMs: 10,
        isPortuguese: () => false,
      });
      service.onNotify((n) => notifications.push(n));

      await service.checkWithRetry({
        connId: "en-db",
        connName: "DatabaseEN",
        maxRetries: 1,
        checkFn: async () => undefined,
        getErrorMessage: () => "Network failure",
      });

      await new Promise((resolve) => setTimeout(resolve, 50));

      const warnings = notifications.filter((n) => n.type === "warning");
      const errors = notifications.filter((n) => n.type === "error");

      expect(warnings[0].message).toContain("could not be established");
      expect(warnings[0].message).toContain("attempt 1 of 1");
      expect(errors[0].message).toContain("after 1 attempts");

      service.dispose();
    });
  });
});

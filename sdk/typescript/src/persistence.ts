export type DurableInboxKind = "message" | "notification";
export interface DurableRecord {
  sequence: number;
  payload: string;
  createdAt: string;
  expiresAt: string | null;
}
export interface DurableTaskStatus {
  taskId: string;
  /** Queued=0, Running=1, Succeeded=2, Failed=3, Cancelled=4. */
  state: 0 | 1 | 2 | 3 | 4;
  progress: string | null;
  error: string | null;
  finishedAt: string | null;
}

export class RealtimeStackPersistenceError extends Error {
  constructor(readonly status: number) {
    super(`Persistence request failed (${status}).`);
    this.name = "RealtimeStackPersistenceError";
  }
}

/** Session-scoped durable reads. Never accepts tenant IDs, owner IDs, or worker credentials. */
export class RealtimeStackPersistenceClient {
  constructor(private readonly basePath = "/realtimestack/persistence", private readonly request: typeof fetch = globalThis.fetch.bind(globalThis)) {
    if (!basePath.startsWith("/") || basePath.startsWith("//") || /[?#{}\\]/u.test(basePath) || basePath.endsWith("/")) {
      throw new TypeError("Persistence basePath must be a same-origin absolute route prefix.");
    }
  }

  async read(streamId: string, kind: DurableInboxKind, afterSequence = 0, limit = 100, signal?: AbortSignal): Promise<DurableRecord[]> {
    this.id(streamId);
    this.sequence(afterSequence);
    if (!Number.isSafeInteger(limit) || limit < 1 || limit > 1000) throw new RangeError("limit must be between 1 and 1000.");
    const response = await this.post("records/read", { streamId, kind: this.kind(kind), afterSequence, limit }, signal);
    return await response.json() as DurableRecord[];
  }

  async acknowledge(streamId: string, kind: DurableInboxKind, throughSequence: number, signal?: AbortSignal): Promise<void> {
    this.id(streamId);
    this.sequence(throughSequence);
    await this.post("records/ack", { streamId, kind: this.kind(kind), throughSequence }, signal);
  }

  async getTaskStatus(taskId: string, signal?: AbortSignal): Promise<DurableTaskStatus | null> {
    this.id(taskId);
    const response = await this.post("tasks/read", { taskId }, signal, true);
    return response.status === 404 ? null : await response.json() as DurableTaskStatus;
  }

  private async post(path: string, body: unknown, signal?: AbortSignal, allowMissing = false): Promise<Response> {
    const response = await this.request(`${this.basePath}/${path}`, {
      method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store",
      headers: { "Content-Type": "application/json" }, body: JSON.stringify(body), ...(signal ? { signal } : {}),
    });
    if (!response.ok && !(allowMissing && response.status === 404)) throw new RealtimeStackPersistenceError(response.status);
    return response;
  }

  private kind(value: DurableInboxKind): number {
    if (value !== "message" && value !== "notification") throw new TypeError("Unknown inbox kind.");
    return value === "message" ? 0 : 1;
  }
  private id(value: string): void {
    if (typeof value !== "string" || value.trim().length === 0 || value.length > 128) throw new TypeError("IDs must contain 1 to 128 characters.");
  }
  private sequence(value: number): void {
    if (!Number.isSafeInteger(value) || value < 0) throw new RangeError("Sequence numbers must be nonnegative safe integers.");
  }
}

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
export declare class RealtimePersistenceError extends Error {
    readonly status: number;
    constructor(status: number);
}
/** Session-scoped durable reads. Never accepts tenant IDs, owner IDs, or worker credentials. */
export declare class RealtimePersistenceClient {
    private readonly basePath;
    private readonly request;
    constructor(basePath?: string, request?: typeof fetch);
    read(streamId: string, kind: DurableInboxKind, afterSequence?: number, limit?: number, signal?: AbortSignal): Promise<DurableRecord[]>;
    acknowledge(streamId: string, kind: DurableInboxKind, throughSequence: number, signal?: AbortSignal): Promise<void>;
    getTaskStatus(taskId: string, signal?: AbortSignal): Promise<DurableTaskStatus | null>;
    private post;
    private kind;
    private id;
    private sequence;
}
//# sourceMappingURL=persistence.d.ts.map
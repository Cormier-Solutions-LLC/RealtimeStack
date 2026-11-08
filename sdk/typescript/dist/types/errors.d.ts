import type { ServerMessageEnvelope } from "./protocol.js";
export declare class RealtimeStackError extends Error {
    readonly code: string;
    readonly correlationId?: string;
    constructor(message: string, code: string, correlationId?: string);
    static fromEnvelope(envelope: ServerMessageEnvelope): RealtimeStackError;
}
export declare class RealtimeStackConnectionError extends RealtimeStackError {
    constructor(message: string, code?: string);
}
export declare class RealtimeStackQueueError extends RealtimeStackError {
    constructor();
}
//# sourceMappingURL=errors.d.ts.map
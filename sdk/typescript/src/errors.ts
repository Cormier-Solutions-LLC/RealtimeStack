import type { ProtocolErrorPayload, ServerMessageEnvelope } from "./protocol.js";

export class RealtimeStackError extends Error {
  public readonly code: string;
  public readonly correlationId?: string;

  public constructor(message: string, code: string, correlationId?: string) {
    super(message);
    this.name = "RealtimeStackError";
    this.code = code;
    if (correlationId !== undefined) {
      this.correlationId = correlationId;
    }
  }

  public static fromEnvelope(envelope: ServerMessageEnvelope): RealtimeStackError {
    const error: ProtocolErrorPayload = envelope.error ?? {
      code: "invalid_envelope",
      message: "The server returned an invalid error envelope.",
    };
    return new RealtimeStackError(error.message, error.code, envelope.correlationId);
  }
}

export class RealtimeStackConnectionError extends RealtimeStackError {
  public constructor(message: string, code = "connection_failed") {
    super(message, code);
    this.name = "RealtimeStackConnectionError";
  }
}

export class RealtimeStackQueueError extends RealtimeStackError {
  public constructor() {
    super("The bounded client command queue is full.", "queue_saturated");
    this.name = "RealtimeStackQueueError";
  }
}

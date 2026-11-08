export interface WebSocketLike {
    readonly readyState: number;
    readonly transport?: RealtimeStackTransport | undefined;
    binaryType: BinaryType;
    onopen: ((event: Event) => void) | null;
    onmessage: ((event: MessageEvent) => void) | null;
    onerror: ((event: Event) => void) | null;
    onclose: ((event: CloseEvent) => void) | null;
    send(data: string): void;
    close(code?: number, reason?: string): void;
}
export type RealtimeStackTransport = "websocket" | "http-streaming";
export interface HttpStreamingOptions {
    readonly url: string | URL;
    readonly fetch?: typeof globalThis.fetch;
}
export interface RealtimeStackTransportOptions {
    readonly transports?: readonly RealtimeStackTransport[];
    readonly httpStreaming?: HttpStreamingOptions;
    readonly webSocketFactory?: (url: string, protocol: string) => WebSocketLike;
}
export declare function createTransportSocket(websocketUrl: string, protocol: string, options: RealtimeStackTransportOptions, nextConnectionUrl?: () => Promise<string>): WebSocketLike;
//# sourceMappingURL=transport.d.ts.map
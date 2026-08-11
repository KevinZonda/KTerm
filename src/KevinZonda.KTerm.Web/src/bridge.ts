export interface SessionCreated {
  sessionId: string;
  shellName: string;
  processId: number;
}

export interface BridgeEvent {
  version: number;
  type: string;
  requestId?: string;
  sessionId?: string;
  payload: Record<string, unknown>;
}

type BridgeEventHandler = (event: BridgeEvent) => void;

interface PendingRequest {
  resolve: (event: BridgeEvent) => void;
  reject: (error: Error) => void;
}

export class NativeBridge {
  private readonly handlers = new Map<string, Set<BridgeEventHandler>>();
  private readonly pending = new Map<string, PendingRequest>();

  public constructor() {
    window.chrome.webview.addEventListener('message', this.handleMessage);
  }

  public async ready(): Promise<void> {
    await this.request('app.ready', {});
  }

  public async createSession(cols = 80, rows = 24): Promise<SessionCreated> {
    const event = await this.request('session.create', { cols, rows });
    if (!event.sessionId) {
      throw new Error('The native host did not return a session ID.');
    }

    return {
      sessionId: event.sessionId,
      shellName: this.payloadString(event, 'shellName') || 'shell',
      processId: this.payloadNumber(event, 'processId')
    };
  }

  public sendInput(sessionId: string, data: string): void {
    this.send('session.input', { data }, sessionId);
  }

  public resize(sessionId: string, cols: number, rows: number): void {
    this.send('session.resize', { cols, rows }, sessionId);
  }

  public closeSession(sessionId: string): void {
    this.send('session.close', {}, sessionId);
  }

  public writeClipboard(text: string): void {
    this.send('clipboard.write', { text });
  }

  public async readClipboard(): Promise<string> {
    const event = await this.request('clipboard.read', {});
    return this.payloadString(event, 'text');
  }

  public on(type: string, handler: BridgeEventHandler): () => void {
    const handlers = this.handlers.get(type) ?? new Set<BridgeEventHandler>();
    handlers.add(handler);
    this.handlers.set(type, handlers);
    return () => handlers.delete(handler);
  }

  private readonly handleMessage = (messageEvent: MessageEvent<unknown>): void => {
    if (!this.isBridgeEvent(messageEvent.data)) {
      return;
    }

    const event = messageEvent.data;
    if (event.requestId) {
      const request = this.pending.get(event.requestId);
      if (request) {
        this.pending.delete(event.requestId);
        if (event.type === 'session.error') {
          request.reject(new Error(this.payloadString(event, 'message') || 'Native operation failed.'));
        } else {
          request.resolve(event);
        }
      }
    }

    this.handlers.get(event.type)?.forEach(handler => handler(event));
  };

  private request(type: string, payload: Record<string, unknown>): Promise<BridgeEvent> {
    const requestId = crypto.randomUUID();
    return new Promise<BridgeEvent>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject });
      this.send(type, payload, undefined, requestId);

      window.setTimeout(() => {
        if (this.pending.delete(requestId)) {
          reject(new Error(`Native request '${type}' timed out.`));
        }
      }, 15_000);
    });
  }

  private send(
    type: string,
    payload: Record<string, unknown>,
    sessionId?: string,
    requestId?: string
  ): void {
    window.chrome.webview.postMessage({
      version: 1,
      type,
      requestId,
      sessionId,
      payload
    });
  }

  private isBridgeEvent(value: unknown): value is BridgeEvent {
    if (typeof value !== 'object' || value === null) {
      return false;
    }

    const candidate = value as Partial<BridgeEvent>;
    return candidate.version === 1 &&
      typeof candidate.type === 'string' &&
      typeof candidate.payload === 'object' &&
      candidate.payload !== null;
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }
}


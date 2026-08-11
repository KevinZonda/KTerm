import { FitAddon } from '@xterm/addon-fit';
import { WebglAddon } from '@xterm/addon-webgl';
import { Terminal } from '@xterm/xterm';
import type { IDisposable } from '@xterm/xterm';
import type { NativeBridge, SessionCreated } from './bridge';

export interface TerminalCallbacks {
  onFocus(sessionId: string): void;
  onClose(sessionId: string): void;
  onTitle(sessionId: string, title: string): void;
}

export class TerminalController {
  public readonly sessionId: string;
  public readonly shellName: string;
  public readonly element: HTMLDivElement;

  private readonly bridge: NativeBridge;
  private readonly callbacks: TerminalCallbacks;
  private readonly terminal: Terminal;
  private readonly fitAddon = new FitAddon();
  private readonly host = document.createElement('div');
  private readonly resizeObserver: ResizeObserver;
  private readonly disposables: IDisposable[] = [];
  private webglAddon?: WebglAddon;
  private opened = false;
  private exited = false;
  private fitTimer?: number;
  private lastCols = 0;
  private lastRows = 0;

  public constructor(session: SessionCreated, bridge: NativeBridge, callbacks: TerminalCallbacks) {
    this.sessionId = session.sessionId;
    this.shellName = session.shellName;
    this.bridge = bridge;
    this.callbacks = callbacks;
    this.element = document.createElement('div');
    this.element.className = 'terminal-pane';
    this.element.dataset.sessionId = session.sessionId;
    this.element.title = `${session.shellName} · PID ${session.processId}`;

    this.host.className = 'terminal-host';
    this.element.append(this.host, this.createCloseButton());

    this.terminal = new Terminal({
      allowProposedApi: false,
      convertEol: false,
      cursorBlink: true,
      cursorStyle: 'bar',
      fontFamily: 'Cascadia Mono, Cascadia Code, Consolas, monospace',
      fontSize: 14,
      lineHeight: 1.12,
      scrollback: 5000,
      theme: {
        background: '#0c0f14',
        foreground: '#d8dee9',
        cursor: '#8fbcbb',
        cursorAccent: '#0c0f14',
        selectionBackground: '#3b5268',
        black: '#1b2028',
        red: '#e06c75',
        green: '#98c379',
        yellow: '#e5c07b',
        blue: '#61afef',
        magenta: '#c678dd',
        cyan: '#56b6c2',
        white: '#abb2bf',
        brightBlack: '#5c6370',
        brightRed: '#e06c75',
        brightGreen: '#98c379',
        brightYellow: '#e5c07b',
        brightBlue: '#61afef',
        brightMagenta: '#c678dd',
        brightCyan: '#56b6c2',
        brightWhite: '#ffffff'
      }
    });
    this.terminal.loadAddon(this.fitAddon);

    this.disposables.push(
      this.terminal.onData(data => this.bridge.sendInput(this.sessionId, data)),
      this.terminal.onTitleChange(title => this.callbacks.onTitle(this.sessionId, title)),
      this.terminal.onResize(size => {
        if (size.cols === this.lastCols && size.rows === this.lastRows) {
          return;
        }

        this.lastCols = size.cols;
        this.lastRows = size.rows;
        this.bridge.resize(this.sessionId, size.cols, size.rows);
      })
    );

    this.element.addEventListener('pointerdown', () => this.focus());
    this.element.addEventListener('focusin', () => this.callbacks.onFocus(this.sessionId));
    this.host.addEventListener('contextmenu', this.handleContextMenu, { capture: true });
    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.element);
  }

  public mount(): void {
    if (!this.element.isConnected) {
      return;
    }

    if (!this.opened) {
      this.terminal.open(this.host);
      this.opened = true;
      this.enableWebgl();
    }

    this.scheduleFit();
  }

  public write(data: string): void {
    this.terminal.write(data);
  }

  public markExited(exitCode: number): void {
    if (this.exited) {
      return;
    }

    this.exited = true;
    this.element.classList.add('exited');
    this.terminal.write(`\r\n\x1b[90m[process exited with code ${exitCode}]\x1b[0m\r\n`);
  }

  public focus(): void {
    this.callbacks.onFocus(this.sessionId);
    this.terminal.focus();
  }

  public setFocused(focused: boolean): void {
    this.element.classList.toggle('focused', focused);
  }

  public scheduleFit(): void {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }

    this.fitTimer = window.setTimeout(() => {
      this.fitTimer = undefined;
      if (!this.opened || !this.element.isConnected ||
          this.element.clientWidth < 20 || this.element.clientHeight < 20) {
        return;
      }

      try {
        this.fitAddon.fit();
      } catch {
        // A detached/transitioning pane will be fitted on its next ResizeObserver event.
      }
    }, 40);
  }

  public copySelection(): boolean {
    if (!this.terminal.hasSelection()) {
      return false;
    }

    this.bridge.writeClipboard(this.terminal.getSelection());
    return true;
  }

  public paste(text: string): void {
    if (text) {
      this.terminal.paste(text);
    }
  }

  public dispose(): void {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }
    this.resizeObserver.disconnect();
    this.disposables.forEach(disposable => disposable.dispose());
    this.webglAddon?.dispose();
    this.terminal.dispose();
    this.element.remove();
  }

  private enableWebgl(): void {
    try {
      const addon = new WebglAddon();
      addon.onContextLoss(() => {
        addon.dispose();
        if (this.webglAddon === addon) {
          this.webglAddon = undefined;
        }
        this.element.classList.add('renderer-fallback');
      });
      this.terminal.loadAddon(addon);
      this.webglAddon = addon;
      this.element.classList.add('renderer-webgl');
    } catch {
      this.element.classList.add('renderer-fallback');
    }
  }

  private readonly handleContextMenu = (event: MouseEvent): void => {
    if (this.copySelection()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.terminal.clearSelection();
      return;
    }

    if (this.terminal.modes.mouseTrackingMode !== 'none') {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    this.focus();
    void this.bridge.readClipboard()
      .then(text => this.paste(text))
      .catch(error => console.error('Unable to paste clipboard text.', error));
  };

  private createCloseButton(): HTMLButtonElement {
    const close = document.createElement('button');
    close.className = 'pane-close';
    close.type = 'button';
    close.title = 'Close pane';
    close.setAttribute('aria-label', 'Close terminal pane');
    close.textContent = '×';
    close.addEventListener('pointerdown', event => event.stopPropagation());
    close.addEventListener('click', event => {
      event.stopPropagation();
      this.callbacks.onClose(this.sessionId);
    });
    return close;
  }
}

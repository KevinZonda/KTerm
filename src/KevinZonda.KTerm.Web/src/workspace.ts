import type { BridgeEvent, NativeBridge, SessionCreated } from './bridge';
import { TerminalController } from './terminal-controller';
import type { TerminalCallbacks } from './terminal-controller';

type SplitDirection = 'columns' | 'rows';

type LayoutNode =
  | { type: 'terminal'; sessionId: string }
  | {
      type: 'split';
      direction: SplitDirection;
      ratio: number;
      first: LayoutNode;
      second: LayoutNode;
    };

interface TabState {
  id: string;
  title: string;
  root: LayoutNode;
  focusedSessionId: string;
}

export class Workspace implements TerminalCallbacks {
  private readonly bridge: NativeBridge;
  private readonly tabStrip: HTMLElement;
  private readonly workspace: HTMLElement;
  private readonly status: HTMLElement;
  private readonly terminals = new Map<string, TerminalController>();
  private readonly earlyOutput = new Map<string, string[]>();
  private readonly tabs: TabState[] = [];
  private activeTabId?: string;
  private operationPending = false;

  public constructor(bridge: NativeBridge) {
    this.bridge = bridge;
    this.tabStrip = this.requireElement('tab-strip');
    this.workspace = this.requireElement('workspace');
    this.status = this.requireElement('status');

    this.bridge.on('session.output', event => this.handleOutput(event));
    this.bridge.on('session.exited', event => this.handleExit(event));
    this.bridge.on('workspace.command', event => this.executeCommand(this.payloadString(event, 'command')));
    this.bridge.on('app.runtimeFailed', event => {
      this.setStatus(`WebView2 process failed: ${this.payloadString(event, 'kind')}`, true);
    });

    window.addEventListener('keydown', this.handleKeyboard, { capture: true });
  }

  public async initialize(): Promise<void> {
    this.setStatus('Starting KTerm…');
    await this.bridge.ready();
    await this.createTab();
    this.setStatus('');
  }

  public async createTab(): Promise<void> {
    await this.runExclusive(async () => {
      this.setStatus('Starting shell…');
      const session = await this.bridge.createSession();
      this.addTerminal(session);

      const tab: TabState = {
        id: crypto.randomUUID(),
        title: session.shellName,
        root: { type: 'terminal', sessionId: session.sessionId },
        focusedSessionId: session.sessionId
      };
      this.tabs.push(tab);
      this.activeTabId = tab.id;
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    });
  }

  public async splitFocused(direction: SplitDirection): Promise<void> {
    const tab = this.activeTab;
    if (!tab) {
      return;
    }

    await this.runExclusive(async () => {
      this.setStatus('Starting split shell…');
      const current = this.terminals.get(tab.focusedSessionId);
      const session = await this.bridge.createSession(
        Math.max(current?.element.clientWidth ? 40 : 80, 40),
        24
      );
      this.addTerminal(session);

      tab.root = this.replaceLeaf(tab.root, tab.focusedSessionId, {
        type: 'split',
        direction,
        ratio: 0.5,
        first: { type: 'terminal', sessionId: tab.focusedSessionId },
        second: { type: 'terminal', sessionId: session.sessionId }
      });
      tab.focusedSessionId = session.sessionId;
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    });
  }

  public onFocus(sessionId: string): void {
    const tab = this.tabs.find(candidate => this.contains(candidate.root, sessionId));
    if (!tab) {
      return;
    }

    tab.focusedSessionId = sessionId;
    this.terminals.forEach((terminal, id) => terminal.setFocused(id === sessionId));
    this.renderTabs();
  }

  public onClose(sessionId: string): void {
    void this.closePane(sessionId);
  }

  public onTitle(sessionId: string, title: string): void {
    const tab = this.tabs.find(candidate => this.contains(candidate.root, sessionId));
    if (tab && tab.focusedSessionId === sessionId && title.trim()) {
      tab.title = title.trim();
      this.renderTabs();
    }
  }

  private readonly handleKeyboard = (event: KeyboardEvent): void => {
    if (event.repeat) {
      return;
    }

    if (event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey) {
      let handled = true;
      switch (event.code) {
        case 'KeyT':
          this.executeCommand('newTab');
          break;
        case 'Backslash':
          this.executeCommand('splitColumns');
          break;
        case 'Minus':
          this.executeCommand('splitRows');
          break;
        default:
          handled = false;
      }

      if (handled) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }
    }

    if (event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey) {
      const terminal = this.focusedTerminal;
      if (event.code === 'KeyC' && terminal?.copySelection()) {
        event.preventDefault();
        event.stopImmediatePropagation();
      } else if (event.code === 'KeyV' && terminal) {
        event.preventDefault();
        event.stopImmediatePropagation();
        void this.bridge.readClipboard().then(text => terminal.paste(text));
      }
    }
  };

  private executeCommand(command: string): void {
    switch (command) {
      case 'newTab':
        void this.createTab();
        break;
      case 'splitColumns':
        void this.splitFocused('columns');
        break;
      case 'splitRows':
        void this.splitFocused('rows');
        break;
    }
  }

  private addTerminal(session: SessionCreated): void {
    const terminal = new TerminalController(session, this.bridge, this);
    this.terminals.set(session.sessionId, terminal);

    const pending = this.earlyOutput.get(session.sessionId);
    if (pending) {
      pending.forEach(data => terminal.write(data));
      this.earlyOutput.delete(session.sessionId);
    }
  }

  private handleOutput(event: BridgeEvent): void {
    if (!event.sessionId) {
      return;
    }

    const data = this.payloadString(event, 'data');
    const terminal = this.terminals.get(event.sessionId);
    if (terminal) {
      terminal.write(data);
    } else {
      const pending = this.earlyOutput.get(event.sessionId) ?? [];
      pending.push(data);
      this.earlyOutput.set(event.sessionId, pending);
    }
  }

  private handleExit(event: BridgeEvent): void {
    if (event.sessionId) {
      this.terminals.get(event.sessionId)?.markExited(this.payloadNumber(event, 'exitCode'));
    }
  }

  private render(): void {
    this.renderTabs();
    const tab = this.activeTab;
    if (!tab) {
      this.workspace.replaceChildren(this.emptyState());
      return;
    }

    this.workspace.replaceChildren(this.renderNode(tab.root));
    this.terminals.forEach((terminal, id) => terminal.setFocused(id === tab.focusedSessionId));

    this.collectSessions(tab.root).forEach(id => this.terminals.get(id)?.mount());
  }

  private renderTabs(): void {
    const fragment = document.createDocumentFragment();
    for (const tab of this.tabs) {
      const tabElement = document.createElement('div');
      tabElement.className = 'tab';
      tabElement.classList.toggle('active', tab.id === this.activeTabId);
      tabElement.addEventListener('pointerdown', event => {
        if (event.button === 1) {
          event.preventDefault();
        }
      });
      tabElement.addEventListener('auxclick', event => {
        if (event.button !== 1) {
          return;
        }

        event.preventDefault();
        event.stopPropagation();
        void this.closeTab(tab.id);
      });

      const activate = document.createElement('button');
      activate.type = 'button';
      activate.className = 'tab-activate';
      activate.title = tab.title;
      activate.textContent = tab.title || 'Terminal';
      activate.addEventListener('click', () => {
        this.activeTabId = tab.id;
        this.render();
        this.focusSession(tab.focusedSessionId);
      });

      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'tab-close';
      close.title = 'Close tab';
      close.setAttribute('aria-label', `Close ${tab.title}`);
      close.textContent = '×';
      close.addEventListener('click', () => void this.closeTab(tab.id));
      tabElement.append(activate, close);
      fragment.append(tabElement);
    }

    const add = document.createElement('button');
    add.type = 'button';
    add.id = 'new-tab';
    add.title = 'New tab (Alt+T)';
    add.setAttribute('aria-label', 'New terminal tab');
    add.textContent = '+';
    add.addEventListener('click', () => void this.createTab());
    fragment.append(add);
    this.tabStrip.replaceChildren(fragment);
  }

  private renderNode(node: LayoutNode): HTMLElement {
    if (node.type === 'terminal') {
      const terminal = this.terminals.get(node.sessionId);
      if (!terminal) {
        const missing = document.createElement('div');
        missing.className = 'terminal-missing';
        missing.textContent = 'Terminal session is unavailable.';
        return missing;
      }
      return terminal.element;
    }

    const split = document.createElement('div');
    split.className = `split split-${node.direction}`;
    const first = this.renderNode(node.first);
    const divider = document.createElement('div');
    divider.className = 'split-divider';
    divider.setAttribute('role', 'separator');
    const second = this.renderNode(node.second);
    split.append(first, divider, second);

    const applyRatio = (): void => {
      if (node.direction === 'columns') {
        split.style.gridTemplateColumns = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      } else {
        split.style.gridTemplateRows = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      }
    };
    applyRatio();

    divider.addEventListener('pointerdown', event => {
      event.preventDefault();
      divider.setPointerCapture(event.pointerId);
      divider.classList.add('dragging');
    });
    divider.addEventListener('pointermove', event => {
      if (!divider.hasPointerCapture(event.pointerId)) {
        return;
      }
      const rect = split.getBoundingClientRect();
      const ratio = node.direction === 'columns'
        ? (event.clientX - rect.left) / rect.width
        : (event.clientY - rect.top) / rect.height;
      node.ratio = Math.min(0.9, Math.max(0.1, ratio));
      applyRatio();
    });
    const finishDrag = (event: PointerEvent): void => {
      if (divider.hasPointerCapture(event.pointerId)) {
        divider.releasePointerCapture(event.pointerId);
      }
      divider.classList.remove('dragging');
      this.fitActiveTerminals();
    };
    divider.addEventListener('pointerup', finishDrag);
    divider.addEventListener('pointercancel', finishDrag);
    return split;
  }

  private async closePane(sessionId: string): Promise<void> {
    const tab = this.tabs.find(candidate => this.contains(candidate.root, sessionId));
    if (!tab) {
      return;
    }

    if (tab.root.type === 'terminal') {
      await this.closeTab(tab.id);
      return;
    }

    const nextRoot = this.removeLeaf(tab.root, sessionId);
    if (!nextRoot) {
      await this.closeTab(tab.id);
      return;
    }

    tab.root = nextRoot;
    const remaining = this.collectSessions(tab.root);
    if (!remaining.includes(tab.focusedSessionId)) {
      tab.focusedSessionId = remaining[0] ?? '';
    }
    this.destroyTerminal(sessionId);
    this.bridge.closeSession(sessionId);
    this.render();
    this.focusSession(tab.focusedSessionId);
  }

  private async closeTab(tabId: string): Promise<void> {
    const index = this.tabs.findIndex(tab => tab.id === tabId);
    if (index < 0) {
      return;
    }

    const [tab] = this.tabs.splice(index, 1);
    if (!tab) {
      return;
    }

    for (const sessionId of this.collectSessions(tab.root)) {
      this.destroyTerminal(sessionId);
      this.bridge.closeSession(sessionId);
    }

    if (this.activeTabId === tabId) {
      this.activeTabId = this.tabs[Math.min(index, this.tabs.length - 1)]?.id;
    }

    this.render();
    const active = this.activeTab;
    if (active) {
      this.focusSession(active.focusedSessionId);
    } else {
      await this.createTab();
    }
  }

  private destroyTerminal(sessionId: string): void {
    this.terminals.get(sessionId)?.dispose();
    this.terminals.delete(sessionId);
    this.earlyOutput.delete(sessionId);
  }

  private focusSession(sessionId: string): void {
    this.terminals.get(sessionId)?.focus();
  }

  private fitActiveTerminals(): void {
    const tab = this.activeTab;
    if (tab) {
      this.collectSessions(tab.root).forEach(id => this.terminals.get(id)?.scheduleFit());
    }
  }

  private replaceLeaf(node: LayoutNode, sessionId: string, replacement: LayoutNode): LayoutNode {
    if (node.type === 'terminal') {
      return node.sessionId === sessionId ? replacement : node;
    }
    return {
      ...node,
      first: this.replaceLeaf(node.first, sessionId, replacement),
      second: this.replaceLeaf(node.second, sessionId, replacement)
    };
  }

  private removeLeaf(node: LayoutNode, sessionId: string): LayoutNode | null {
    if (node.type === 'terminal') {
      return node.sessionId === sessionId ? null : node;
    }

    const first = this.removeLeaf(node.first, sessionId);
    const second = this.removeLeaf(node.second, sessionId);
    if (!first) {
      return second;
    }
    if (!second) {
      return first;
    }
    return { ...node, first, second };
  }

  private contains(node: LayoutNode, sessionId: string): boolean {
    return node.type === 'terminal'
      ? node.sessionId === sessionId
      : this.contains(node.first, sessionId) || this.contains(node.second, sessionId);
  }

  private collectSessions(node: LayoutNode): string[] {
    return node.type === 'terminal'
      ? [node.sessionId]
      : [...this.collectSessions(node.first), ...this.collectSessions(node.second)];
  }

  private async runExclusive(operation: () => Promise<void>): Promise<void> {
    if (this.operationPending) {
      return;
    }
    this.operationPending = true;
    try {
      await operation();
    } catch (error) {
      this.setStatus(error instanceof Error ? error.message : String(error), true);
    } finally {
      this.operationPending = false;
    }
  }

  private emptyState(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'empty-state';
    element.textContent = 'No terminal tabs are open.';
    return element;
  }

  private requireElement(id: string): HTMLElement {
    const element = document.getElementById(id);
    if (!element) {
      throw new Error(`Missing application element '#${id}'.`);
    }
    return element;
  }

  private setStatus(message: string, error = false): void {
    this.status.textContent = message;
    this.status.classList.toggle('visible', Boolean(message));
    this.status.classList.toggle('error', error);
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }

  private get activeTab(): TabState | undefined {
    return this.tabs.find(tab => tab.id === this.activeTabId);
  }

  private get focusedTerminal(): TerminalController | undefined {
    const tab = this.activeTab;
    return tab ? this.terminals.get(tab.focusedSessionId) : undefined;
  }
}

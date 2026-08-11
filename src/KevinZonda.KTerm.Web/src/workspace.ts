import { DEFAULT_SETTINGS } from './bridge';
import type { AppSettings, BridgeEvent, NativeBridge, SessionCreated } from './bridge';
import { TerminalController } from './terminal-controller';
import type { TerminalCallbacks } from './terminal-controller';
import { applyTerminalThemeToDocument } from './themes';

type SplitDirection = 'columns' | 'rows';

type LayoutNode =
  | { type: 'pane'; paneId: string }
  | {
      type: 'split';
      direction: SplitDirection;
      ratio: number;
      first: LayoutNode;
      second: LayoutNode;
    };

interface TerminalTabState {
  sessionId: string;
  title: string;
}

interface PaneState {
  id: string;
  tabs: TerminalTabState[];
  activeSessionId: string;
}

export class Workspace implements TerminalCallbacks {
  private readonly bridge: NativeBridge;
  private readonly workspace: HTMLElement;
  private readonly status: HTMLElement;
  private readonly terminals = new Map<string, TerminalController>();
  private readonly earlyOutput = new Map<string, string[]>();
  private readonly panes = new Map<string, PaneState>();
  private readonly paneElements = new Map<string, HTMLElement>();
  private root?: LayoutNode;
  private focusedPaneId?: string;
  private settings: AppSettings = structuredClone(DEFAULT_SETTINGS);
  private operationPending = false;

  public constructor(bridge: NativeBridge) {
    this.bridge = bridge;
    this.workspace = this.requireElement('workspace');
    this.status = this.requireElement('status');

    this.bridge.on('session.output', event => this.handleOutput(event));
    this.bridge.on('session.exited', event => this.handleExit(event));
    this.bridge.on('workspace.command', event => this.executeCommand(this.payloadString(event, 'command')));
    this.bridge.on('app.settingsChanged', event => this.applySettings(this.bridge.settingsFrom(event)));
    this.bridge.on('app.runtimeFailed', event => {
      this.setStatus(`WebView2 process failed: ${this.payloadString(event, 'kind')}`, true);
    });

    window.addEventListener('keydown', this.handleKeyboard, { capture: true });
  }

  public async initialize(): Promise<void> {
    this.setStatus('Starting KTerm…');
    this.applySettings(await this.bridge.ready());
    await this.createTab();
    this.setStatus('');
  }

  public async createTab(paneId = this.focusedPaneId): Promise<void> {
    await this.runExclusive(() => this.createTabInPane(paneId));
  }

  public async splitFocused(direction: SplitDirection): Promise<void> {
    const pane = this.focusedPane;
    const root = this.root;
    if (!pane || !root) {
      return;
    }

    await this.runExclusive(async () => {
      this.setStatus('Starting split shell…');
      const current = this.terminals.get(pane.activeSessionId);
      const session = await this.bridge.createSession(
        current?.element.clientWidth ? 40 : 80,
        24
      );
      this.addTerminal(session);

      const newPane: PaneState = {
        id: crypto.randomUUID(),
        tabs: [this.createTerminalTab(session)],
        activeSessionId: session.sessionId
      };
      this.panes.set(newPane.id, newPane);
      this.root = this.replacePaneLeaf(root, pane.id, {
        type: 'split',
        direction,
        ratio: 0.5,
        first: { type: 'pane', paneId: pane.id },
        second: { type: 'pane', paneId: newPane.id }
      });
      this.focusedPaneId = newPane.id;
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    });
  }

  public onFocus(sessionId: string): void {
    const pane = this.findPaneBySession(sessionId);
    if (!pane) {
      return;
    }

    pane.activeSessionId = sessionId;
    this.focusedPaneId = pane.id;
    this.updateFocusState();
  }

  public onTitle(sessionId: string, title: string): void {
    const pane = this.findPaneBySession(sessionId);
    const tab = pane?.tabs.find(candidate => candidate.sessionId === sessionId);
    if (!pane || !tab || !title.trim()) {
      return;
    }

    tab.title = title.trim();
    this.refreshPaneTabs(pane);
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
        case 'KeyS':
          this.bridge.openSettings();
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

  private async createTabInPane(paneId?: string): Promise<void> {
    this.setStatus('Starting shell…');
    const session = await this.bridge.createSession();
    this.addTerminal(session);

    let pane = paneId ? this.panes.get(paneId) : this.focusedPane;
    if (!pane) {
      pane = {
        id: crypto.randomUUID(),
        tabs: [],
        activeSessionId: session.sessionId
      };
      this.panes.set(pane.id, pane);
      this.root = { type: 'pane', paneId: pane.id };
    }

    pane.tabs.push(this.createTerminalTab(session));
    pane.activeSessionId = session.sessionId;
    this.focusedPaneId = pane.id;
    this.render();
    this.focusSession(session.sessionId);
    this.setStatus('');
  }

  private createTerminalTab(session: SessionCreated): TerminalTabState {
    return {
      sessionId: session.sessionId,
      title: session.shellName
    };
  }

  private addTerminal(session: SessionCreated): void {
    const terminal = new TerminalController(
      session,
      this.bridge,
      this,
      this.settings.font,
      this.settings.theme
    );
    this.terminals.set(session.sessionId, terminal);

    const pending = this.earlyOutput.get(session.sessionId);
    if (pending) {
      pending.forEach(data => terminal.write(data));
      this.earlyOutput.delete(session.sessionId);
    }
  }

  private applySettings(settings: AppSettings): void {
    this.settings = settings;
    applyTerminalThemeToDocument(settings.theme.name);
    this.terminals.forEach(terminal => {
      terminal.applyFontSettings(settings.font);
      terminal.applyThemeSettings(settings.theme);
    });
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
    this.paneElements.clear();
    if (!this.root) {
      this.workspace.replaceChildren(this.emptyState());
      return;
    }

    this.workspace.replaceChildren(this.renderNode(this.root));
    this.updateFocusState();

    for (const paneId of this.collectPaneIds(this.root)) {
      const pane = this.panes.get(paneId);
      if (pane) {
        this.terminals.get(pane.activeSessionId)?.mount();
      }
    }
  }

  private renderNode(node: LayoutNode): HTMLElement {
    if (node.type === 'pane') {
      const pane = this.panes.get(node.paneId);
      return pane ? this.renderPane(pane) : this.missingPane();
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
      this.fitVisibleTerminals();
    };
    divider.addEventListener('pointerup', finishDrag);
    divider.addEventListener('pointercancel', finishDrag);
    return split;
  }

  private renderPane(pane: PaneState): HTMLElement {
    const element = document.createElement('section');
    element.className = 'pane';
    element.dataset.paneId = pane.id;
    element.addEventListener('pointerdown', () => {
      this.focusedPaneId = pane.id;
      this.updateFocusState();
    });

    const tabStrip = document.createElement('header');
    tabStrip.className = 'pane-tab-strip';
    tabStrip.setAttribute('aria-label', 'Pane terminal tabs');
    element.append(tabStrip);
    this.renderPaneTabs(pane, tabStrip);

    const content = document.createElement('div');
    content.className = 'pane-content';
    const terminal = this.terminals.get(pane.activeSessionId);
    content.append(terminal?.element ?? this.missingTerminal());
    element.append(content);

    this.paneElements.set(pane.id, element);
    return element;
  }

  private renderPaneTabs(pane: PaneState, tabStrip: HTMLElement): void {
    const fragment = document.createDocumentFragment();
    for (const tab of pane.tabs) {
      const tabElement = document.createElement('div');
      tabElement.className = 'pane-tab';
      tabElement.classList.toggle('active', tab.sessionId === pane.activeSessionId);
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
        this.closeTerminalTab(pane.id, tab.sessionId);
      });

      const activate = document.createElement('button');
      activate.type = 'button';
      activate.className = 'pane-tab-activate';
      activate.title = tab.title;
      activate.textContent = tab.title || 'Terminal';
      activate.addEventListener('click', () => this.activateTab(pane.id, tab.sessionId));

      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'pane-tab-close';
      close.title = 'Close tab';
      close.setAttribute('aria-label', `Close ${tab.title}`);
      close.textContent = '×';
      close.addEventListener('click', event => {
        event.stopPropagation();
        this.closeTerminalTab(pane.id, tab.sessionId);
      });
      tabElement.append(activate, close);
      fragment.append(tabElement);
    }

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'pane-new-tab';
    add.title = 'New tab in this pane (Alt+T)';
    add.setAttribute('aria-label', 'New terminal tab in this pane');
    add.textContent = '+';
    add.addEventListener('click', () => void this.createTab(pane.id));
    fragment.append(add);
    tabStrip.replaceChildren(fragment);
  }

  private refreshPaneTabs(pane: PaneState): void {
    const tabStrip = this.paneElements.get(pane.id)?.querySelector<HTMLElement>('.pane-tab-strip');
    if (tabStrip) {
      this.renderPaneTabs(pane, tabStrip);
    }
  }

  private activateTab(paneId: string, sessionId: string): void {
    const pane = this.panes.get(paneId);
    if (!pane || !pane.tabs.some(tab => tab.sessionId === sessionId)) {
      return;
    }

    pane.activeSessionId = sessionId;
    this.focusedPaneId = pane.id;
    this.render();
    this.focusSession(sessionId);
  }

  private closeTerminalTab(paneId: string, sessionId: string): void {
    void this.runExclusive(async () => {
      const pane = this.panes.get(paneId);
      const index = pane?.tabs.findIndex(tab => tab.sessionId === sessionId) ?? -1;
      if (!pane || index < 0) {
        return;
      }

      const wasActive = pane.activeSessionId === sessionId;
      pane.tabs.splice(index, 1);
      this.destroyTerminal(sessionId);
      this.bridge.closeSession(sessionId);

      if (pane.tabs.length > 0) {
        if (wasActive) {
          pane.activeSessionId = pane.tabs[Math.min(index, pane.tabs.length - 1)]!.sessionId;
        }
        this.focusedPaneId = pane.id;
        this.render();
        this.focusSession(pane.activeSessionId);
        return;
      }

      const nextPaneId = this.root
        ? this.findClosestSiblingPaneId(this.root, pane.id)
        : undefined;
      this.panes.delete(pane.id);
      this.root = this.root ? this.removePaneLeaf(this.root, pane.id) ?? undefined : undefined;
      this.focusedPaneId = nextPaneId ?? (this.root ? this.firstPaneId(this.root) : undefined);
      if (!this.root) {
        await this.createTabInPane();
        return;
      }

      this.render();
      const focused = this.focusedPane;
      if (focused) {
        this.focusSession(focused.activeSessionId);
      }
    });
  }

  private destroyTerminal(sessionId: string): void {
    this.terminals.get(sessionId)?.dispose();
    this.terminals.delete(sessionId);
    this.earlyOutput.delete(sessionId);
  }

  private focusSession(sessionId: string): void {
    this.terminals.get(sessionId)?.focus();
  }

  private updateFocusState(): void {
    this.paneElements.forEach((element, paneId) => {
      element.classList.toggle('focused', paneId === this.focusedPaneId);
    });

    const focusedSessionId = this.focusedPane?.activeSessionId;
    this.terminals.forEach((terminal, sessionId) => {
      terminal.setFocused(sessionId === focusedSessionId);
    });
  }

  private fitVisibleTerminals(): void {
    this.panes.forEach(pane => {
      this.terminals.get(pane.activeSessionId)?.scheduleFit();
    });
  }

  private replacePaneLeaf(node: LayoutNode, paneId: string, replacement: LayoutNode): LayoutNode {
    if (node.type === 'pane') {
      return node.paneId === paneId ? replacement : node;
    }
    return {
      ...node,
      first: this.replacePaneLeaf(node.first, paneId, replacement),
      second: this.replacePaneLeaf(node.second, paneId, replacement)
    };
  }

  private removePaneLeaf(node: LayoutNode, paneId: string): LayoutNode | null {
    if (node.type === 'pane') {
      return node.paneId === paneId ? null : node;
    }

    const first = this.removePaneLeaf(node.first, paneId);
    const second = this.removePaneLeaf(node.second, paneId);
    if (!first) {
      return second;
    }
    if (!second) {
      return first;
    }
    return { ...node, first, second };
  }

  private collectPaneIds(node: LayoutNode): string[] {
    return node.type === 'pane'
      ? [node.paneId]
      : [...this.collectPaneIds(node.first), ...this.collectPaneIds(node.second)];
  }

  private firstPaneId(node: LayoutNode): string {
    return node.type === 'pane' ? node.paneId : this.firstPaneId(node.first);
  }

  private findClosestSiblingPaneId(node: LayoutNode, paneId: string): string | undefined {
    if (node.type === 'pane') {
      return undefined;
    }

    if (this.containsPane(node.first, paneId)) {
      return this.findClosestSiblingPaneId(node.first, paneId) ?? this.firstPaneId(node.second);
    }
    if (this.containsPane(node.second, paneId)) {
      return this.findClosestSiblingPaneId(node.second, paneId) ?? this.firstPaneId(node.first);
    }
    return undefined;
  }

  private containsPane(node: LayoutNode, paneId: string): boolean {
    return node.type === 'pane'
      ? node.paneId === paneId
      : this.containsPane(node.first, paneId) || this.containsPane(node.second, paneId);
  }

  private findPaneBySession(sessionId: string): PaneState | undefined {
    return [...this.panes.values()].find(
      pane => pane.tabs.some(tab => tab.sessionId === sessionId)
    );
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
    element.textContent = 'No terminal panes are open.';
    return element;
  }

  private missingPane(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'pane-missing';
    element.textContent = 'Terminal pane is unavailable.';
    return element;
  }

  private missingTerminal(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'terminal-missing';
    element.textContent = 'Terminal session is unavailable.';
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

  private get focusedPane(): PaneState | undefined {
    return this.focusedPaneId ? this.panes.get(this.focusedPaneId) : undefined;
  }

  private get focusedTerminal(): TerminalController | undefined {
    const pane = this.focusedPane;
    return pane ? this.terminals.get(pane.activeSessionId) : undefined;
  }
}

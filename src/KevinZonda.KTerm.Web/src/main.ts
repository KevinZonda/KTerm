import '@xterm/xterm/css/xterm.css';
import './styles.css';
import { NativeBridge } from './bridge';
import { Workspace } from './workspace';

const bridge = new NativeBridge();
const workspace = new Workspace(bridge);

const resizeEdges = [
  'top',
  'right',
  'bottom',
  'left',
  'top-left',
  'top-right',
  'bottom-right',
  'bottom-left'
] as const;

for (const edge of resizeEdges) {
  const handle = document.createElement('div');
  handle.className = `window-resize-handle window-resize-${edge}`;
  handle.addEventListener('pointerdown', event => {
    if (event.button !== 0) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    bridge.beginWindowResize(edge);
  });
  document.body.append(handle);
}

void workspace.initialize().catch(error => {
  const status = document.getElementById('status');
  if (status) {
    status.textContent = error instanceof Error ? error.message : String(error);
    status.classList.add('visible', 'error');
  }
});

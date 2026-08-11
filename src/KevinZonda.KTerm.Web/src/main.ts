import '@xterm/xterm/css/xterm.css';
import './styles.css';
import { NativeBridge } from './bridge';
import { Workspace } from './workspace';

const bridge = new NativeBridge();
const workspace = new Workspace(bridge);

void workspace.initialize().catch(error => {
  const status = document.getElementById('status');
  if (status) {
    status.textContent = error instanceof Error ? error.message : String(error);
    status.classList.add('visible', 'error');
  }
});


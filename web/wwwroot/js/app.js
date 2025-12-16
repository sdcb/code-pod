// Docker Shell Host - Main Application Logic

// Global state
let currentSessionId = null;
let systemStatus = null;
let connection = null;

// ========== SignalR Connection ==========
async function initSignalR() {
    connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/status")
        .withAutomaticReconnect()
        .build();

    connection.on("StatusUpdated", (status) => {
        console.log("Status update received:", status);
        systemStatus = status;
        updateDashboard(status);
    });

    connection.on("ContainerStatusChanged", (container) => {
        console.log("Container status changed:", container);
    });

    connection.onreconnecting(() => {
        UI.updateConnectionStatus(false, "Reconnecting...");
    });

    connection.onreconnected(() => {
        UI.updateConnectionStatus(true, "Connected");
        loadStatus();
    });

    connection.onclose(() => {
        UI.updateConnectionStatus(false, "Disconnected");
    });

    try {
        await connection.start();
        UI.updateConnectionStatus(true, "Connected");
        console.log("SignalR connected");
    } catch (err) {
        console.error("SignalR connection failed:", err);
        UI.updateConnectionStatus(false, "Connection failed");
    }
}

// ========== Page Navigation ==========
function initNavigation() {
    document.querySelectorAll('[data-page]').forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            const page = link.dataset.page;
            
            document.querySelectorAll('[data-page]').forEach(l => l.classList.remove('active'));
            link.classList.add('active');
            
            document.querySelectorAll('.page-section').forEach(s => s.classList.remove('active'));
            document.getElementById(page + 'Section').classList.add('active');
            
            if (page === 'dashboard') loadStatus();
            if (page === 'sessions') loadSessions();
            if (page === 'containers') loadContainers();
        });
    });
}

// ========== Dashboard ==========
async function loadStatus() {
    try {
        systemStatus = await Api.getStatus();
        updateDashboard(systemStatus);
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

function updateDashboard(status) {
    document.getElementById('availableContainers').textContent = status.availableContainers;
    document.getElementById('maxContainers').textContent = status.maxContainers;
    document.getElementById('activeSessions').textContent = status.activeSessions;
    document.getElementById('warmingContainers').textContent = status.warmingContainers;
    document.getElementById('destroyingContainers').textContent = status.destroyingContainers;
    
    updateContainersTable('containersTableBody', status.containers || []);
}

function updateContainersTable(tableId, containers) {
    const tbody = document.getElementById(tableId);
    if (!containers || containers.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" class="text-center text-muted py-4">No containers</td></tr>';
        return;
    }

    tbody.innerHTML = containers.map(c => `
        <tr>
            <td><code>${c.shortId || c.containerId?.substring(0, 12)}</code></td>
            <td>${UI.getStatusBadge(c.status)}</td>
            <td><span class="badge bg-secondary">${c.dockerStatus || '-'}</span></td>
            <td>${c.sessionId ? `<code>${c.sessionId.substring(0, 8)}</code>` : '<span class="text-muted">-</span>'}</td>
            <td>${UI.formatDate(c.createdAt)}</td>
            <td>
                <button class="btn btn-sm btn-outline-danger" onclick="deleteContainer('${c.containerId}')">
                    <i class="bi bi-trash"></i>
                </button>
            </td>
        </tr>
    `).join('');
}

// ========== Container Operations ==========
async function createContainer() {
    try {
        await Api.createContainer();
        UI.showToast('Success', 'Container creation started', 'success');
        await loadStatus();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

async function deleteContainer(containerId) {
    if (!confirm('Are you sure you want to delete this container?')) return;
    
    try {
        await Api.deleteContainer(containerId);
        UI.showToast('Success', 'Container deleted', 'success');
        await loadStatus();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

async function deleteAllContainers() {
    if (!confirm('Are you sure you want to delete all containers? This will destroy all sessions!')) return;
    
    try {
        await Api.deleteAllContainers();
        UI.showToast('Success', 'All containers deleted', 'success');
        await loadStatus();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

async function loadContainers() {
    try {
        const containers = await Api.getContainers();
        updateContainersTable('allContainersTableBody', containers);
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

// ========== Session Operations ==========
async function loadSessions() {
    try {
        const sessions = await Api.getSessions();
        updateSessionsList(sessions);
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

function updateSessionsList(sessions) {
    const list = document.getElementById('sessionsList');
    if (!sessions || sessions.length === 0) {
        list.innerHTML = '<div class="text-center text-muted py-4">No sessions</div>';
        return;
    }

    list.innerHTML = sessions.map(s => `
        <a href="#" class="list-group-item list-group-item-action ${s.sessionId === currentSessionId ? 'active' : ''}" 
           onclick="selectSession('${s.sessionId}'); return false;">
            <div class="d-flex justify-content-between align-items-center">
                <div>
                    <strong>${s.name || 'Unnamed session'}</strong>
                    <div class="small ${s.sessionId === currentSessionId ? 'text-white-50' : 'text-muted'}">${s.sessionId.substring(0, 8)}...</div>
                </div>
                <span class="badge ${s.status === 1 || s.status === 'Active' ? 'bg-success' : s.status === 0 || s.status === 'Queued' ? 'bg-warning' : 'bg-secondary'}">
                    ${s.status === 1 || s.status === 'Active' ? 'Active' : s.status === 0 || s.status === 'Queued' ? 'Queued' : 'Destroyed'}
                </span>
            </div>
        </a>
    `).join('');
}

async function createSession() {
    try {
        const session = await Api.createSession(`Session-${Date.now().toString(36)}`);
        UI.showToast('Success', 'Session created', 'success');
        await loadSessions();
        selectSession(session.sessionId);
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

async function selectSession(sessionId) {
    currentSessionId = sessionId;
    
    try {
        const session = await Api.getSession(sessionId);
        showSessionDetail(session);
        await loadSessions();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

function showSessionDetail(session) {
    document.getElementById('noSessionSelected').style.display = 'none';
    document.getElementById('sessionDetailCard').style.display = 'block';
    
    document.getElementById('sessionDetailId').textContent = session.sessionId;
    document.getElementById('sessionDetailStatus').innerHTML = 
        session.status === 1 || session.status === 'Active' ? 
        '<span class="badge bg-success">Active</span>' : 
        '<span class="badge bg-warning">Queued</span>';
    document.getElementById('sessionDetailContainerId').textContent = 
        session.containerId ? session.containerId.substring(0, 12) : '-';
    document.getElementById('sessionDetailCommandCount').textContent = session.commandCount || 0;
    
    document.getElementById('commandOutput').innerHTML = '<span class="text-muted">Output will appear here...</span>';
    document.getElementById('filePreviewSection').style.display = 'none';
}

async function destroyCurrentSession() {
    if (!currentSessionId) return;
    if (!confirm('Are you sure you want to destroy this session?')) return;
    
    try {
        await Api.deleteSession(currentSessionId);
        UI.showToast('Success', 'Session destroyed', 'success');
        currentSessionId = null;
        document.getElementById('sessionDetailCard').style.display = 'none';
        document.getElementById('noSessionSelected').style.display = 'block';
        await loadSessions();
        await loadStatus();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

// ========== Command Execution ==========
let currentCommandAbortController = null;

function executeCommand() {
    if (!currentSessionId) return;
    
    const input = document.getElementById('commandInput');
    const command = input.value.trim();
    if (!command) return;

    const output = document.getElementById('commandOutput');
    
    // 取消之前的命令（如果有）
    if (currentCommandAbortController) {
        currentCommandAbortController.abort();
        currentCommandAbortController = null;
    }

    // 清空输出并显示执行状态
    output.innerHTML = '<span class="text-muted">Executing...</span>';
    input.value = '';

    let stdoutContent = '';
    let stderrContent = '';
    let hasOutput = false;

    // 使用 SSE 流式执行命令
    currentCommandAbortController = Api.executeCommandStream(currentSessionId, command, {
        onStdout: (data) => {
            hasOutput = true;
            stdoutContent += data;
            updateCommandOutput(output, stdoutContent, stderrContent, null, null);
        },
        onStderr: (data) => {
            hasOutput = true;
            stderrContent += data;
            updateCommandOutput(output, stdoutContent, stderrContent, null, null);
        },
        onExit: (exitCode, executionTimeMs) => {
            currentCommandAbortController = null;
            updateCommandOutput(output, stdoutContent, stderrContent, exitCode, executionTimeMs);
            
            // 更新命令计数
            const countEl = document.getElementById('sessionDetailCommandCount');
            if (countEl) {
                countEl.textContent = parseInt(countEl.textContent || '0') + 1;
            }
        },
        onError: (error) => {
            currentCommandAbortController = null;
            output.innerHTML = `<div class="stderr">Error: ${UI.escapeHtml(error.message)}</div>`;
        }
    }, 60);
}

function updateCommandOutput(outputEl, stdout, stderr, exitCode, executionTimeMs) {
    let html = '';
    
    if (stdout) {
        html += `<div class="stdout">${UI.escapeHtml(stdout)}</div>`;
    }
    
    if (stderr) {
        html += `<div class="stderr">${UI.escapeHtml(stderr)}</div>`;
    }
    
    if (!stdout && !stderr && exitCode !== null) {
        html = '<span class="text-muted">(no output)</span>';
    }
    
    if (exitCode !== null) {
        const exitClass = exitCode === 0 ? '' : 'error';
        html += `<div class="exit-code ${exitClass}">Exit code: ${exitCode} | Duration: ${executionTimeMs}ms</div>`;
    }
    
    outputEl.innerHTML = html || '<span class="text-muted">Executing...</span>';
}

// ========== File Operations ==========
async function listDirectory() {
    if (!currentSessionId) return;
    
    const path = document.getElementById('currentPath').value || '/app';
    const browser = document.getElementById('fileBrowser');
    browser.innerHTML = '<div class="text-center py-3"><div class="spinner-border spinner-border-sm"></div></div>';
    
    try {
        const result = await Api.listFiles(currentSessionId, path);
        
        if (!result.entries || result.entries.length === 0) {
            browser.innerHTML = '<div class="text-center text-muted py-3">Directory is empty</div>';
            return;
        }

        browser.innerHTML = result.entries.map(entry => {
            const fullPath = (path + '/' + entry.name).replace(/\/+/g, '/');
            if (entry.isDirectory) {
                return `
                    <div class="file-item" onclick="navigateTo('${fullPath}')">
                        <i class="bi bi-folder-fill text-warning"></i>
                        <span>${entry.name}</span>
                    </div>
                `;
            } else {
                return `
                    <div class="file-item">
                        <i class="bi bi-file-text text-secondary"></i>
                        <span onclick="previewFile('${fullPath}')" style="cursor:pointer">${entry.name}</span>
                        ${entry.size ? `<small class="ms-2 text-muted">${UI.formatSize(entry.size)}</small>` : ''}
                        <div class="file-actions">
                            <a href="${Api.getDownloadUrl(currentSessionId, fullPath)}" 
                               class="btn btn-sm btn-outline-primary" download title="Download">
                                <i class="bi bi-download"></i>
                            </a>
                            <button class="btn btn-sm btn-outline-danger" onclick="deleteFile('${fullPath}')" title="Delete">
                                <i class="bi bi-trash"></i>
                            </button>
                        </div>
                    </div>
                `;
            }
        }).join('');
        
        document.getElementById('currentPath').value = path;
    } catch (error) {
        browser.innerHTML = `<div class="text-center text-danger py-3">${UI.escapeHtml(error.message)}</div>`;
    }
}

function navigateTo(path) {
    document.getElementById('currentPath').value = path.replace(/\/+/g, '/');
    listDirectory();
}

function navigateUp() {
    const path = document.getElementById('currentPath').value;
    const parts = path.split('/').filter(p => p);
    if (parts.length > 0) {
        parts.pop();
    }
    document.getElementById('currentPath').value = '/' + parts.join('/');
    listDirectory();
}

async function previewFile(path) {
    if (!currentSessionId) return;
    
    const section = document.getElementById('filePreviewSection');
    const content = document.getElementById('filePreviewContent');
    const fileName = document.getElementById('previewFileName');
    
    section.style.display = 'block';
    fileName.textContent = path.split('/').pop();
    content.innerHTML = '<div class="text-center py-3"><div class="spinner-border spinner-border-sm"></div> Loading...</div>';
    
    try {
        const result = await Api.downloadFile(currentSessionId, path);
        
        const textTypes = ['text/', 'application/json', 'application/xml', 'application/javascript'];
        const isText = textTypes.some(t => result.contentType?.startsWith(t)) || 
                       result.contentType === 'application/octet-stream' && result.size < 100000;
        
        if (isText) {
            try {
                const text = atob(result.contentBase64);
                content.innerHTML = `<pre>${UI.escapeHtml(text)}</pre>`;
            } catch (e) {
                content.innerHTML = '<div class="text-muted">Cannot preview this file (may be binary)</div>';
            }
        } else {
            content.innerHTML = `<div class="text-muted">
                <p>File type: ${result.contentType}</p>
                <p>File size: ${UI.formatSize(result.size)}</p>
                <p>This file type is not supported for preview</p>
                <a href="${Api.getDownloadUrl(currentSessionId, path)}" class="btn btn-sm btn-primary" download>
                    <i class="bi bi-download"></i> Download file
                </a>
            </div>`;
        }
    } catch (error) {
        content.innerHTML = `<div class="text-danger">Load failed: ${UI.escapeHtml(error.message)}</div>`;
    }
}

async function deleteFile(path) {
    if (!currentSessionId) return;
    if (!confirm(`Are you sure you want to delete ${path}?`)) return;
    
    try {
        await Api.deleteFile(currentSessionId, path);
        UI.showToast('Success', 'File deleted', 'success');
        listDirectory();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

// ========== File Upload ==========
function initFileUpload() {
    const uploadZone = document.getElementById('uploadZone');
    const fileInput = document.getElementById('fileInput');
    
    if (!uploadZone || !fileInput) return;
    
    // Click upload zone to trigger file selection
    uploadZone.addEventListener('click', () => fileInput.click());
    
    // Drag and drop upload
    uploadZone.addEventListener('dragover', (e) => {
        e.preventDefault();
        uploadZone.classList.add('dragover');
    });
    
    uploadZone.addEventListener('dragleave', () => {
        uploadZone.classList.remove('dragover');
    });
    
    uploadZone.addEventListener('drop', (e) => {
        e.preventDefault();
        uploadZone.classList.remove('dragover');
        handleFiles(e.dataTransfer.files);
    });
    
    // File selection
    fileInput.addEventListener('change', () => {
        handleFiles(fileInput.files);
        fileInput.value = ''; // Clear to allow selecting the same file again
    });
}

async function handleFiles(files) {
    if (!currentSessionId) {
        UI.showToast('Error', 'Please select a session first', 'error');
        return;
    }
    
    const currentPath = document.getElementById('currentPath').value || '/app';
    const progressContainer = document.getElementById('uploadProgress');
    const progressBar = progressContainer?.querySelector('.progress-bar');
    const progressText = document.getElementById('uploadProgressText');
    
    if (progressContainer) progressContainer.style.display = 'block';
    
    let uploaded = 0;
    const total = files.length;
    
    for (const file of files) {
        try {
            if (progressText) progressText.textContent = `Uploading: ${file.name} (${uploaded + 1}/${total})`;
            if (progressBar) progressBar.style.width = `${(uploaded / total) * 100}%`;
            
            const content = await readFileAsBase64(file);
            const targetPath = (currentPath + '/' + file.name).replace(/\/+/g, '/');
            
            await Api.uploadFile(currentSessionId, targetPath, content);
            uploaded++;
            
            if (progressBar) progressBar.style.width = `${(uploaded / total) * 100}%`;
        } catch (error) {
            UI.showToast('Upload failed', `${file.name}: ${error.message}`, 'error');
        }
    }
    
    if (progressContainer) {
        setTimeout(() => {
            progressContainer.style.display = 'none';
            if (progressBar) progressBar.style.width = '0%';
        }, 1000);
    }
    
    if (uploaded > 0) {
        UI.showToast('Success', `Uploaded ${uploaded} file(s)`, 'success');
        listDirectory();
    }
}

function readFileAsBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => {
            const base64 = reader.result.split(',')[1];
            resolve(base64);
        };
        reader.onerror = reject;
        reader.readAsDataURL(file);
    });
}

// ========== Create New File ==========
async function createNewFile() {
    if (!currentSessionId) {
        UI.showToast('Error', 'Please select a session first', 'error');
        return;
    }
    
    const fileName = prompt('Enter file name:');
    if (!fileName) return;
    
    const currentPath = document.getElementById('currentPath').value || '/app';
    const targetPath = (currentPath + '/' + fileName).replace(/\/+/g, '/');
    
    try {
        // Create empty file
        await Api.uploadFile(currentSessionId, targetPath, btoa(''));
        UI.showToast('Success', `File ${fileName} created`, 'success');
        listDirectory();
    } catch (error) {
        UI.showToast('Error', error.message, 'error');
    }
}

// ========== Initialization ===========
document.addEventListener('DOMContentLoaded', async () => {
    initNavigation();
    initFileUpload();
    await initSignalR();
    await loadStatus();
});

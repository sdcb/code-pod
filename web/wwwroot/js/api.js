// Docker Shell Host - API Module
// Encapsulates all API calls

const Api = {
    // Base API call
    async call(url, options = {}) {
        try {
            const response = await fetch(url, {
                headers: { 'Content-Type': 'application/json' },
                ...options
            });
            const data = await response.json();
            if (!data.success) {
                throw new Error(data.message || data.error || 'Operation failed');
            }
            return data.data;
        } catch (error) {
            console.error('API Error:', error);
            throw error;
        }
    },

    // ========== System Status ==========
    async getStatus() {
        return this.call('/api/admin/status');
    },

    // ========== Container Management ==========
    async getContainers() {
        return this.call('/api/admin/containers');
    },

    async createContainer() {
        return this.call('/api/admin/containers', { method: 'POST' });
    },

    async deleteContainer(containerId) {
        return this.call(`/api/admin/containers/${containerId}`, { method: 'DELETE' });
    },

    async deleteAllContainers() {
        return this.call('/api/admin/containers', { method: 'DELETE' });
    },

    // ========== Session Management ==========
    async getSessions() {
        return this.call('/api/sessions');
    },

    async getSession(sessionId) {
        return this.call(`/api/sessions/${sessionId}`);
    },

    async createSession(name) {
        return this.call('/api/sessions', {
            method: 'POST',
            body: JSON.stringify({ name })
        });
    },

    async deleteSession(sessionId) {
        return this.call(`/api/sessions/${sessionId}`, { method: 'DELETE' });
    },

    // ========== Command Execution ==========
    async executeCommand(sessionId, command, timeoutSeconds = 30) {
        return this.call(`/api/sessions/${sessionId}/commands`, {
            method: 'POST',
            body: JSON.stringify({ command, timeoutSeconds })
        });
    },

    /**
     * 执行命令（SSE 流式响应）
     * @param {string} sessionId - 会话 ID
     * @param {string} command - 要执行的命令
     * @param {Object} callbacks - 回调函数对象
     * @param {function(string): void} callbacks.onStdout - 标准输出回调
     * @param {function(string): void} callbacks.onStderr - 标准错误回调
     * @param {function(number, number): void} callbacks.onExit - 命令完成回调 (exitCode, executionTimeMs)
     * @param {function(Error): void} callbacks.onError - 错误回调
     * @param {number} timeoutSeconds - 超时时间（秒）
     * @returns {AbortController} - 用于取消请求的控制器
     */
    executeCommandStream(sessionId, command, callbacks, timeoutSeconds = 60) {
        const abortController = new AbortController();
        
        fetch(`/api/sessions/${sessionId}/commands/stream`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ command, timeoutSeconds }),
            signal: abortController.signal
        }).then(async response => {
            if (!response.ok) {
                const errorData = await response.json().catch(() => ({}));
                throw new Error(errorData.error || `HTTP ${response.status}`);
            }

            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            while (true) {
                const { done, value } = await reader.read();
                if (done) break;

                buffer += decoder.decode(value, { stream: true });
                
                // 解析 SSE 事件
                const lines = buffer.split('\n');
                buffer = lines.pop(); // 保留未完成的行

                let currentEvent = null;
                let currentData = '';

                for (const line of lines) {
                    if (line.startsWith('event:')) {
                        currentEvent = line.slice(6).trim();
                    } else if (line.startsWith('data:')) {
                        currentData = line.slice(5).trim();
                    } else if (line === '' && currentData) {
                        // 空行表示事件结束
                        try {
                            const data = JSON.parse(currentData);
                            
                            switch (currentEvent) {
                                case 'stdout':
                                    if (callbacks.onStdout && data.data) {
                                        callbacks.onStdout(data.data);
                                    }
                                    break;
                                case 'stderr':
                                    if (callbacks.onStderr && data.data) {
                                        callbacks.onStderr(data.data);
                                    }
                                    break;
                                case 'exit':
                                    if (callbacks.onExit) {
                                        callbacks.onExit(data.exitCode, data.executionTimeMs);
                                    }
                                    break;
                            }
                        } catch (e) {
                            console.warn('Failed to parse SSE data:', currentData, e);
                        }
                        currentEvent = null;
                        currentData = '';
                    }
                }
            }
        }).catch(error => {
            if (error.name !== 'AbortError' && callbacks.onError) {
                callbacks.onError(error);
            }
        });

        return abortController;
    },

    // ========== File Operations ==========
    async listFiles(sessionId, path) {
        return this.call(`/api/sessions/${sessionId}/files/list?path=${encodeURIComponent(path)}`);
    },

    async downloadFile(sessionId, path) {
        return this.call(`/api/sessions/${sessionId}/files/download?path=${encodeURIComponent(path)}`);
    },

    getDownloadUrl(sessionId, path) {
        return `/api/sessions/${sessionId}/files/download-raw?path=${encodeURIComponent(path)}`;
    },

    async uploadFile(sessionId, path, contentBase64) {
        // Use form upload endpoint
        const blob = base64ToBlob(contentBase64);
        const formData = new FormData();
        const fileName = path.split('/').pop();
        formData.append('file', blob, fileName);
        
        const response = await fetch(`/api/sessions/${sessionId}/files/upload?targetPath=${encodeURIComponent(path.substring(0, path.lastIndexOf('/')) || '/app')}`, {
            method: 'POST',
            body: formData
        });
        const data = await response.json();
        if (!data.success) {
            throw new Error(data.message || data.error || 'Operation failed');
        }
        return data.data;
    },

    async deleteFile(sessionId, path) {
        return this.call(`/api/sessions/${sessionId}/files?path=${encodeURIComponent(path)}`, {
            method: 'DELETE'
        });
    }
};

// Helper function to convert base64 to blob
function base64ToBlob(base64, contentType = 'application/octet-stream') {
    const byteCharacters = atob(base64);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
        byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    return new Blob([byteArray], { type: contentType });
}

// Export to global
window.Api = Api;

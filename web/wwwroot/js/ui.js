// Docker Shell Host - UI Utilities Module

const UI = {
    // Show loading overlay
    showLoading() {
        document.getElementById('loadingOverlay').classList.add('show');
    },

    hideLoading() {
        document.getElementById('loadingOverlay').classList.remove('show');
    },

    // Toast notification
    showToast(title, message, type = 'info') {
        const toast = document.getElementById('toast');
        const icon = document.getElementById('toastIcon');
        document.getElementById('toastTitle').textContent = title;
        document.getElementById('toastBody').textContent = message;
        
        icon.className = 'bi me-2 ' + (type === 'error' ? 'bi-x-circle text-danger' : 
            type === 'success' ? 'bi-check-circle text-success' : 'bi-info-circle text-info');
        
        new bootstrap.Toast(toast).show();
    },

    // HTML escape
    escapeHtml(text) {
        if (text === null || text === undefined) return '';
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    },

    // Format file size
    formatSize(bytes) {
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        return (bytes / 1024 / 1024).toFixed(1) + ' MB';
    },

    // Format date
    formatDate(dateStr) {
        if (!dateStr) return '-';
        const date = new Date(dateStr);
        return date.toLocaleString('en-US', { 
            month: '2-digit', day: '2-digit', 
            hour: '2-digit', minute: '2-digit' 
        });
    },

    // Get status badge
    getStatusBadge(status) {
        const statusMap = {
            0: { text: 'Warming', class: 'bg-warning' },
            1: { text: 'Idle', class: 'bg-success' },
            2: { text: 'Busy', class: 'bg-primary' },
            3: { text: 'Destroying', class: 'bg-danger' },
            'Warming': { text: 'Warming', class: 'bg-warning' },
            'Idle': { text: 'Idle', class: 'bg-success' },
            'Busy': { text: 'Busy', class: 'bg-primary' },
            'Destroying': { text: 'Destroying', class: 'bg-danger' }
        };
        const s = statusMap[status] || { text: status, class: 'bg-secondary' };
        return `<span class="badge ${s.class} container-status-badge">${s.text}</span>`;
    },

    // Update connection status
    updateConnectionStatus(connected, text) {
        const el = document.getElementById('connectionStatus');
        el.className = 'connection-status ' + (connected ? 'connected' : 'disconnected');
        el.querySelector('.status-text').textContent = text;
    }
};

// Export to global
window.UI = UI;

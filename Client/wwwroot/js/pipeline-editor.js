// Pipeline visual editor - JS Interop for drag & connect
window.pipelineEditor = {
    _dotNetRef: null,
    _canvas: null,
    _dragging: null,        // { nodeId, offsetX, offsetY }
    _connecting: null,       // { fromModuleId, fromPort, startX, startY }
    _tempLine: null,         // SVG line element for temp connection
    _panning: null,          // { startX, startY, scrollLeft, scrollTop }

    init: function (dotNetRef, canvasId) {
        this._dotNetRef = dotNetRef;
        this._canvas = document.getElementById(canvasId);
        if (!this._canvas) return;

        // Prevent context menu on canvas
        this._canvas.addEventListener('contextmenu', e => e.preventDefault());

        // Mouse move on canvas (for drag, connect & pan)
        this._canvas.addEventListener('mousemove', e => this._onMouseMove(e));
        this._canvas.addEventListener('mouseup', e => this._onMouseUp(e));

        // Pan: mousedown on empty canvas area
        this._canvas.addEventListener('mousedown', e => {
            if (this._dragging || this._connecting) return;
            const target = e.target;
            // Only start pan on canvas background, not on nodes/ports/connections
            if (target.closest('.pipeline-node') || target.closest('[data-port-id]') || target.closest('path')) return;
            this._panning = {
                startX: e.clientX,
                startY: e.clientY,
                scrollLeft: this._canvas.scrollLeft,
                scrollTop: this._canvas.scrollTop
            };
            this._canvas.classList.add('panning');
            e.preventDefault();
        });

        // Touch support
        this._canvas.addEventListener('touchstart', e => {
            if (this._dragging || this._connecting) return;
            const target = e.target;
            if (target.closest('.pipeline-node') || target.closest('[data-port-id]') || target.closest('path')) return;
            const touch = e.touches[0];
            this._panning = {
                startX: touch.clientX,
                startY: touch.clientY,
                scrollLeft: this._canvas.scrollLeft,
                scrollTop: this._canvas.scrollTop
            };
            this._canvas.classList.add('panning');
        }, { passive: false });

        this._canvas.addEventListener('touchmove', e => {
            if (this._panning) {
                e.preventDefault();
                const touch = e.touches[0];
                this._canvas.scrollLeft = this._panning.scrollLeft - (touch.clientX - this._panning.startX);
                this._canvas.scrollTop = this._panning.scrollTop - (touch.clientY - this._panning.startY);
                return;
            }
            if (this._dragging || this._connecting) {
                e.preventDefault();
                const touch = e.touches[0];
                this._onMouseMove({ clientX: touch.clientX, clientY: touch.clientY });
            }
        }, { passive: false });

        this._canvas.addEventListener('touchend', e => {
            if (this._panning) {
                this._panning = null;
                this._canvas.classList.remove('panning');
                return;
            }
            this._onMouseUp(e);
        });
    },

    startNodeDrag: function (nodeId, clientX, clientY) {
        const node = document.querySelector(`[data-node-id="${nodeId}"]`);
        if (!node) return;
        const canvasRect = this._canvas.getBoundingClientRect();
        const nodeRect = node.getBoundingClientRect();
        this._dragging = {
            nodeId: nodeId,
            offsetX: clientX - nodeRect.left,
            offsetY: clientY - nodeRect.top,
            canvasLeft: canvasRect.left,
            canvasTop: canvasRect.top,
            scrollLeft: this._canvas.scrollLeft,
            scrollTop: this._canvas.scrollTop
        };
        node.classList.add('dragging');
    },

    startConnection: function (moduleId, portId, isInput, clientX, clientY) {
        const canvasRect = this._canvas.getBoundingClientRect();
        this._connecting = {
            moduleId: moduleId,
            portId: portId,
            isInput: isInput,
            startX: clientX - canvasRect.left + this._canvas.scrollLeft,
            startY: clientY - canvasRect.top + this._canvas.scrollTop
        };

        // Create temp SVG line
        const svg = this._canvas.querySelector('.pipeline-svg');
        if (svg) {
            this._tempLine = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            this._tempLine.setAttribute('class', 'temp-connection');
            this._tempLine.setAttribute('fill', 'none');
            this._tempLine.setAttribute('stroke', '#6c63ff');
            this._tempLine.setAttribute('stroke-width', '2');
            this._tempLine.setAttribute('stroke-dasharray', '6 3');
            svg.appendChild(this._tempLine);
        }
    },

    getPortCenter: function (moduleId, portId) {
        const port = document.querySelector(`[data-module-id="${moduleId}"][data-port-id="${portId}"]`);
        if (!port || !this._canvas) return null;
        const canvasRect = this._canvas.getBoundingClientRect();
        const portRect = port.getBoundingClientRect();
        return {
            x: portRect.left + portRect.width / 2 - canvasRect.left + this._canvas.scrollLeft,
            y: portRect.top + portRect.height / 2 - canvasRect.top + this._canvas.scrollTop
        };
    },

    _onMouseMove: function (e) {
        if (!this._canvas) return;

        // Canvas panning
        if (this._panning) {
            this._canvas.scrollLeft = this._panning.scrollLeft - (e.clientX - this._panning.startX);
            this._canvas.scrollTop = this._panning.scrollTop - (e.clientY - this._panning.startY);
            return;
        }

        const canvasRect = this._canvas.getBoundingClientRect();
        const mx = e.clientX - canvasRect.left + this._canvas.scrollLeft;
        const my = e.clientY - canvasRect.top + this._canvas.scrollTop;

        // Node dragging
        if (this._dragging) {
            const x = e.clientX - this._dragging.canvasLeft + this._canvas.scrollLeft - this._dragging.offsetX;
            const y = e.clientY - this._dragging.canvasTop + this._canvas.scrollTop - this._dragging.offsetY;
            const node = document.querySelector(`[data-node-id="${this._dragging.nodeId}"]`);
            if (node) {
                node.style.left = Math.max(0, x) + 'px';
                node.style.top = Math.max(0, y) + 'px';
            }
            // Notify Blazor of position change (throttled)
            if (!this._dragThrottle) {
                this._dragThrottle = setTimeout(() => {
                    this._dotNetRef.invokeMethodAsync('OnNodeMoved', this._dragging.nodeId, Math.max(0, x), Math.max(0, y));
                    this._dragThrottle = null;
                }, 30);
            }
            return;
        }

        // Connection dragging
        if (this._connecting && this._tempLine) {
            const sx = this._connecting.startX;
            const sy = this._connecting.startY;
            const dx = Math.abs(mx - sx) * 0.5;
            const path = this._connecting.isInput
                ? `M${mx},${my} C${mx + dx},${my} ${sx - dx},${sy} ${sx},${sy}`
                : `M${sx},${sy} C${sx + dx},${sy} ${mx - dx},${my} ${mx},${my}`;
            this._tempLine.setAttribute('d', path);

            // Highlight compatible ports
            this._highlightCompatiblePorts(e.clientX, e.clientY);
        }
    },

    _onMouseUp: function (e) {
        // End panning
        if (this._panning) {
            this._panning = null;
            this._canvas.classList.remove('panning');
            return;
        }

        // End node drag
        if (this._dragging) {
            const node = document.querySelector(`[data-node-id="${this._dragging.nodeId}"]`);
            if (node) node.classList.remove('dragging');
            // Final position notification
            const canvasRect = this._canvas.getBoundingClientRect();
            const clientX = e.clientX || (e.changedTouches && e.changedTouches[0].clientX) || 0;
            const clientY = e.clientY || (e.changedTouches && e.changedTouches[0].clientY) || 0;
            const x = clientX - canvasRect.left + this._canvas.scrollLeft - this._dragging.offsetX;
            const y = clientY - canvasRect.top + this._canvas.scrollTop - this._dragging.offsetY;
            this._dotNetRef.invokeMethodAsync('OnNodeMoved', this._dragging.nodeId, Math.max(0, x), Math.max(0, y));
            this._dragging = null;
            if (this._dragThrottle) { clearTimeout(this._dragThrottle); this._dragThrottle = null; }
            return;
        }

        // End connection drag
        if (this._connecting) {
            // Check if we're over a port
            const clientX = e.clientX || (e.changedTouches && e.changedTouches[0].clientX) || 0;
            const clientY = e.clientY || (e.changedTouches && e.changedTouches[0].clientY) || 0;
            const target = document.elementFromPoint(clientX, clientY);
            const portEl = target?.closest('[data-port-id]');

            if (portEl) {
                const targetModuleId = portEl.getAttribute('data-module-id');
                const targetPortId = portEl.getAttribute('data-port-id');
                const targetIsInput = portEl.getAttribute('data-port-input') === 'true';

                // Must connect output -> input (or input -> output)
                if (targetIsInput !== this._connecting.isInput && targetModuleId !== this._connecting.moduleId) {
                    const fromModule = this._connecting.isInput ? targetModuleId : this._connecting.moduleId;
                    const fromPort = this._connecting.isInput ? targetPortId : this._connecting.portId;
                    const toModule = this._connecting.isInput ? this._connecting.moduleId : targetModuleId;
                    const toPort = this._connecting.isInput ? this._connecting.portId : targetPortId;
                    this._dotNetRef.invokeMethodAsync('OnConnectionMade', fromModule, fromPort, toModule, toPort);
                }
            }

            // Clean up temp line
            if (this._tempLine) {
                this._tempLine.remove();
                this._tempLine = null;
            }
            this._connecting = null;
            this._clearPortHighlights();
        }
    },

    _highlightCompatiblePorts: function (clientX, clientY) {
        // Simple: highlight all ports of opposite direction
        const allPorts = this._canvas.querySelectorAll('[data-port-id]');
        allPorts.forEach(p => {
            const isInput = p.getAttribute('data-port-input') === 'true';
            if (isInput !== this._connecting.isInput && p.getAttribute('data-module-id') !== this._connecting.moduleId) {
                p.classList.add('port-compatible');
            } else {
                p.classList.remove('port-compatible');
            }
        });
    },

    _clearPortHighlights: function () {
        const allPorts = this._canvas.querySelectorAll('[data-port-id]');
        allPorts.forEach(p => p.classList.remove('port-compatible'));
    },

    dispose: function () {
        this._dotNetRef = null;
        this._canvas = null;
        this._dragging = null;
        this._connecting = null;
        this._panning = null;
    }
};

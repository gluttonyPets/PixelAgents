// Pipeline visual editor — Drawflow wrapper for Blazor interop
window.pipelineEditor = {
    _editor: null,
    _dotNetRef: null,
    _portMap: {},       // drawflowNodeId → { inputs: [portId...], outputs: [portId...] }
    _moduleMap: {},     // drawflowNodeId → moduleId (guid string)
    _reverseMap: {},    // moduleId → drawflowNodeId
    _suppressEvents: false,

    init: function (dotNetRef, containerId) {
        var container = document.getElementById(containerId);
        if (!container) return;

        this._editor = new Drawflow(container);
        this._editor.reroute = false;
        this._editor.force_first_input = false;
        this._editor.start();
        this._dotNetRef = dotNetRef;

        // ── Infinite canvas: allow panning even when .drawflow has scrolled out of view ──
        var editorRef = this._editor;
        var precanvas = container.querySelector('.drawflow');
        var panActive = false;

        container.addEventListener('mousedown', function (e) {
            var target = e.target;
            if (target.closest('.drawflow-node') || target.closest('.connection') ||
                target.classList.contains('input') || target.classList.contains('output')) {
                return;
            }

            editorRef.editor_selected = true;
            editorRef.pos_x = e.clientX;
            editorRef.pos_y = e.clientY;
            editorRef.pos_x_start = e.clientX;
            editorRef.pos_y_start = e.clientY;
            panActive = true;
            e.preventDefault();
        });

        document.addEventListener('mousemove', function (e) {
            if (!panActive || !editorRef.editor_selected) return;

            var x = editorRef.canvas_x + (-(editorRef.pos_x - e.clientX));
            var y = editorRef.canvas_y + (-(editorRef.pos_y - e.clientY));
            editorRef.dispatch('translate', { x: x, y: y });
            precanvas.style.transform = "translate(" + x + "px, " + y + "px) scale(" + editorRef.zoom + ")";
        });

        document.addEventListener('mouseup', function (e) {
            if (!panActive) return;

            if (editorRef.editor_selected) {
                editorRef.canvas_x = editorRef.canvas_x + (-(editorRef.pos_x - e.clientX));
                editorRef.canvas_y = editorRef.canvas_y + (-(editorRef.pos_y - e.clientY));
                editorRef.editor_selected = false;
            }
            panActive = false;
        });

        this._editor.on('nodeMoved', id => {
            if (this._suppressEvents) return;
            var info = this._editor.getNodeFromId(id);
            var moduleId = this._moduleMap[id];
            if (moduleId && info)
                this._dotNetRef.invokeMethodAsync('OnNodeMoved', moduleId, info.pos_x, info.pos_y);
        });

        this._editor.on('connectionCreated', conn => {
            if (this._suppressEvents) return;
            var fromPorts = this._portMap[conn.output_id];
            var toPorts = this._portMap[conn.input_id];
            if (!fromPorts || !toPorts) return;
            var fromIdx = parseInt(conn.output_class.replace('output_', '')) - 1;
            var toIdx = parseInt(conn.input_class.replace('input_', '')) - 1;
            var fromPortId = fromPorts.outputs[fromIdx];
            var toPortId = toPorts.inputs[toIdx];
            var fromModuleId = this._moduleMap[conn.output_id];
            var toModuleId = this._moduleMap[conn.input_id];
            if (fromPortId && toPortId)
                this._dotNetRef.invokeMethodAsync('OnConnectionMade', fromModuleId, fromPortId, toModuleId, toPortId);
        });

        this._editor.on('connectionRemoved', conn => {
            if (this._suppressEvents) return;
            var fromPorts = this._portMap[conn.output_id];
            var toPorts = this._portMap[conn.input_id];
            if (!fromPorts || !toPorts) return;
            var fromIdx = parseInt(conn.output_class.replace('output_', '')) - 1;
            var toIdx = parseInt(conn.input_class.replace('input_', '')) - 1;
            var fromPortId = fromPorts.outputs[fromIdx];
            var toPortId = toPorts.inputs[toIdx];
            var fromModuleId = this._moduleMap[conn.output_id];
            var toModuleId = this._moduleMap[conn.input_id];
            if (fromPortId && toPortId)
                this._dotNetRef.invokeMethodAsync('OnConnectionRemoved', fromModuleId, fromPortId, toModuleId, toPortId);
        });

        this._editor.on('nodeSelected', id => {
            var moduleId = this._moduleMap[id];
            if (moduleId) this._dotNetRef.invokeMethodAsync('OnNodeSelected', moduleId);
        });

        this._editor.on('nodeUnselected', () => {
            this._dotNetRef.invokeMethodAsync('OnNodeDeselected');
        });

        // ── Re-drag existing connections ──
        // When mousedown on an input port that already has a connection,
        // remove that connection and start a new drag from the original output.
        this._setupConnectionRedrag(container);
    },

    _setupConnectionRedrag: function (container) {
        var self = this;
        container.addEventListener('mousedown', function (e) {
            if (!self._editor || self._suppressEvents) return;
            var target = e.target;
            // Only handle input ports
            if (!target.classList.contains('input')) return;

            // Find the parent node
            var nodeEl = target.closest('.drawflow-node');
            if (!nodeEl) return;
            var inputNodeId = parseInt(nodeEl.id.replace('node-', ''));
            var inputClass = target.classList[1]; // e.g. 'input_1'
            if (!inputClass) return;

            // Check if this input has an existing connection
            var nodeData = self._editor.getNodeFromId(inputNodeId);
            if (!nodeData || !nodeData.inputs[inputClass]) return;
            var conns = nodeData.inputs[inputClass].connections;
            if (!conns || conns.length === 0) return;

            // Get the source output info before removing
            var existingConn = conns[0];
            var outputNodeId = parseInt(existingConn.node);
            var outputClass = existingConn.output; // e.g. 'output_1'

            // Remove the existing connection
            self._editor.removeSingleConnection(outputNodeId, inputNodeId, outputClass, inputClass);

            // Find the output port element and simulate a new drag from it
            var outputPortEl = document.querySelector('#node-' + outputNodeId + ' .' + outputClass);
            if (!outputPortEl) return;

            // Prevent the default input port mousedown from firing
            e.stopPropagation();
            e.preventDefault();

            // Trigger Drawflow's connection drawing from the output port
            // We simulate a click event on the output to start the connection
            var rect = outputPortEl.getBoundingClientRect();
            var fakeDown = new MouseEvent('mousedown', {
                bubbles: true,
                clientX: rect.left + rect.width / 2,
                clientY: rect.top + rect.height / 2
            });
            outputPortEl.dispatchEvent(fakeDown);
        }, true); // capture phase to intercept before Drawflow
    },

    addNode: function (moduleId, name, moduleType, color, icon, inputPortsJson, outputPortsJson, x, y, stepOrder, modelName) {
        if (!this._editor) return -1;
        var inputPorts = JSON.parse(inputPortsJson);
        var outputPorts = JSON.parse(outputPortsJson);
        var html = this._buildNodeHtml(name, moduleType, color, icon, stepOrder, modelName);
        var nodeId = this._editor.addNode(
            moduleId, inputPorts.length, outputPorts.length,
            x, y, 'df-type-' + moduleType.toLowerCase(),
            { moduleId: moduleId }, html
        );
        this._portMap[nodeId] = { inputs: inputPorts.map(p => p.id), outputs: outputPorts.map(p => p.id) };
        this._moduleMap[nodeId] = moduleId;
        this._reverseMap[moduleId] = nodeId;
        return nodeId;
    },

    addConnection: function (fromModuleId, fromPortId, toModuleId, toPortId) {
        if (!this._editor) return;
        var fromNodeId = this._reverseMap[fromModuleId];
        var toNodeId = this._reverseMap[toModuleId];
        if (fromNodeId === undefined || toNodeId === undefined) return;
        var fromPorts = this._portMap[fromNodeId];
        var toPorts = this._portMap[toNodeId];
        if (!fromPorts || !toPorts) return;
        var fromIdx = fromPorts.outputs.indexOf(fromPortId) + 1;
        var toIdx = toPorts.inputs.indexOf(toPortId) + 1;
        if (fromIdx > 0 && toIdx > 0) {
            var nodeModule = this._editor.getModuleFromNodeId(fromNodeId);
            if (nodeModule && this._editor.module !== nodeModule) {
                this._editor.changeModule(nodeModule);
            }
            var wasSuppressed = this._suppressEvents;
            this._suppressEvents = true;
            try {
                this._editor.addConnection(fromNodeId, toNodeId, 'output_' + fromIdx, 'input_' + toIdx);
            } catch (e) { }
            this._suppressEvents = wasSuppressed;
        }
    },

    removeNodeByModuleId: function (moduleId) {
        if (!this._editor) return;
        var nodeId = this._reverseMap[moduleId];
        if (nodeId !== undefined) {
            this._suppressEvents = true;
            this._editor.removeNodeId('node-' + nodeId);
            delete this._portMap[nodeId];
            delete this._moduleMap[nodeId];
            delete this._reverseMap[moduleId];
            this._suppressEvents = false;
        }
    },

    updateNodePosition: function (moduleId, x, y) {
        if (!this._editor) return;
        var nodeId = this._reverseMap[moduleId];
        if (nodeId === undefined) return;
        var el = document.querySelector('#node-' + nodeId);
        if (el) {
            el.style.left = x + 'px';
            el.style.top = y + 'px';
            this._editor.drawflow.drawflow.Home.data[nodeId].pos_x = x;
            this._editor.drawflow.drawflow.Home.data[nodeId].pos_y = y;
            this._editor.updateConnectionNodes('node-' + nodeId);
        }
    },

    refreshConnections: function () {
        if (!this._editor) return;
        var data = this._editor.drawflow.drawflow.Home.data;
        for (var nodeId in data) {
            this._editor.updateConnectionNodes('node-' + nodeId);
        }
    },

    removeConnection: function (fromModuleId, fromPortId, toModuleId, toPortId) {
        if (!this._editor) return;
        var fromNodeId = this._reverseMap[fromModuleId];
        var toNodeId = this._reverseMap[toModuleId];
        if (fromNodeId === undefined || toNodeId === undefined) return;
        var fromPorts = this._portMap[fromNodeId];
        var toPorts = this._portMap[toNodeId];
        if (!fromPorts || !toPorts) return;
        var fromIdx = fromPorts.outputs.indexOf(fromPortId) + 1;
        var toIdx = toPorts.inputs.indexOf(toPortId) + 1;
        if (fromIdx > 0 && toIdx > 0) {
            var wasSuppressed = this._suppressEvents;
            this._suppressEvents = true;
            try {
                this._editor.removeSingleConnection(fromNodeId, toNodeId, 'output_' + fromIdx, 'input_' + toIdx);
            } catch (e) { }
            this._suppressEvents = wasSuppressed;
        }
    },

    setSuppressEvents: function (suppress) {
        this._suppressEvents = suppress;
    },

    clear: function () {
        if (this._editor) {
            var wasSuppressed = this._suppressEvents;
            this._suppressEvents = true;
            this._editor.clear();
            this._suppressEvents = wasSuppressed;
            if (!this._editor.drawflow.drawflow['Home']) {
                this._editor.drawflow.drawflow['Home'] = { data: {} };
            }
            this._editor.module = 'Home';
        }
        this._portMap = {};
        this._moduleMap = {};
        this._reverseMap = {};
    },

    // ── Update step order badge on a node without full rebuild ──
    updateNodeStepOrder: function (moduleId, stepLabel) {
        var nodeId = this._reverseMap[moduleId];
        if (nodeId === undefined) return;
        var el = document.querySelector('#node-' + nodeId + ' .df-order-badge');
        if (el) {
            el.textContent = stepLabel || '';
            el.style.display = stepLabel ? '' : 'none';
        }
    },

    // ── Set execution state on a node: 'idle', 'running', 'completed', 'failed' ──
    setNodeState: function (moduleId, state) {
        var nodeId = this._reverseMap[moduleId];
        if (nodeId === undefined) return;
        var el = document.querySelector('#node-' + nodeId);
        if (!el) return;
        el.classList.remove('df-state-running', 'df-state-completed', 'df-state-failed');
        if (state && state !== 'idle') {
            el.classList.add('df-state-' + state);
        }
    },

    // ── Set connection state: 'active' (green animated) or 'idle' ──
    setConnectionState: function (fromModuleId, toModuleId, state) {
        if (!this._editor) return;
        var fromNodeId = this._reverseMap[fromModuleId];
        var toNodeId = this._reverseMap[toModuleId];
        if (fromNodeId === undefined || toNodeId === undefined) return;
        // Find SVG connection paths between these two nodes
        var container = this._editor.precanvas;
        if (!container) return;
        var paths = container.querySelectorAll('.connection');
        paths.forEach(function (conn) {
            var classes = conn.classList;
            if (classes.contains('node_out_node-' + fromNodeId) && classes.contains('node_in_node-' + toNodeId)) {
                if (state === 'active') {
                    conn.classList.add('df-conn-active');
                } else {
                    conn.classList.remove('df-conn-active');
                }
            }
        });
    },

    // ── Clear all execution states ──
    clearAllStates: function () {
        if (!this._editor) return;
        var container = this._editor.precanvas;
        if (!container) return;
        container.querySelectorAll('.df-state-running, .df-state-completed, .df-state-failed').forEach(function (el) {
            el.classList.remove('df-state-running', 'df-state-completed', 'df-state-failed');
        });
        container.querySelectorAll('.df-conn-active').forEach(function (el) {
            el.classList.remove('df-conn-active');
        });
    },

    _buildNodeHtml: function (name, type, color, icon, stepLabel, modelName) {
        var orderBadge = stepLabel
            ? '<span class="df-order-badge">' + stepLabel + '</span>'
            : '<span class="df-order-badge" style="display:none"></span>';
        var modelLine = modelName
            ? '<div class="df-node-model">' + modelName + '</div>'
            : '';
        return '<div class="df-node-content">'
            + '<div class="df-node-overlay-spinner"></div>'
            + '<div class="df-node-title">' + orderBadge + icon + ' ' + name + '</div>'
            + '<div class="df-node-type">' + type + '</div>'
            + modelLine
            + '</div>';
    },

    dispose: function () {
        if (this._editor) {
            this._editor.clear();
            this._editor = null;
        }
        this._dotNetRef = null;
        this._portMap = {};
        this._moduleMap = {};
        this._reverseMap = {};
    }
};

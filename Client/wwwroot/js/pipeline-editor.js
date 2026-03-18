// Pipeline visual editor — Drawflow wrapper for Blazor interop
window.pipelineEditor = {
    _editor: null,
    _dotNetRef: null,
    _portMap: {},       // drawflowNodeId → { inputs: [portId...], outputs: [portId...] }
    _moduleMap: {},     // drawflowNodeId → moduleId (guid string)
    _reverseMap: {},    // moduleId → drawflowNodeId
    _suppressEvents: false, // true while bulk-loading to avoid feedback loops

    init: function (dotNetRef, containerId) {
        var container = document.getElementById(containerId);
        if (!container) return;

        this._editor = new Drawflow(container);
        this._editor.reroute = false;
        this._editor.force_first_input = false;
        this._editor.start();
        this._dotNetRef = dotNetRef;

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
    },

    addNode: function (moduleId, name, moduleType, color, icon, inputPortsJson, outputPortsJson, x, y) {
        if (!this._editor) return -1;
        var inputPorts = JSON.parse(inputPortsJson);
        var outputPorts = JSON.parse(outputPortsJson);
        var html = this._buildNodeHtml(name, moduleType, color, icon, inputPorts, outputPorts);
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
            this._suppressEvents = true;
            try {
                this._editor.addConnection(fromNodeId, toNodeId, 'output_' + fromIdx, 'input_' + toIdx);
            } catch (e) { /* connection may already exist */ }
            this._suppressEvents = false;
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
        // Force recalculate all connection SVG paths after DOM settles
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
            this._suppressEvents = true;
            try {
                this._editor.removeSingleConnection(fromNodeId, toNodeId, 'output_' + fromIdx, 'input_' + toIdx);
            } catch (e) { }
            this._suppressEvents = false;
        }
    },

    setSuppressEvents: function (suppress) {
        this._suppressEvents = suppress;
    },

    clear: function () {
        if (this._editor) {
            this._suppressEvents = true;
            this._editor.clear();
            this._suppressEvents = false;
        }
        this._portMap = {};
        this._moduleMap = {};
        this._reverseMap = {};
    },

    _buildNodeHtml: function (name, type, color, icon, inputs, outputs) {
        var html = '<div class="df-node-content">';
        html += '<div class="df-node-header" style="background:' + color + '">';
        html += '<span class="df-node-title" title="' + name + '">' + icon + ' ' + name + '</span>';
        html += '<span class="df-node-badge">' + type + '</span>';
        html += '</div>';
        // Port rows: one row per max(inputs, outputs) to align with native circles
        var maxPorts = Math.max(inputs.length, outputs.length);
        for (var r = 0; r < maxPorts; r++) {
            html += '<div class="df-port-row">';
            if (r < inputs.length) {
                html += '<span class="df-port-in">';
                html += '<span class="df-port-dot" style="color:' + inputs[r].color + '">&#x25CF;</span> ';
                html += inputs[r].label;
                if (inputs[r].required) html += ' <span class="df-port-req">*</span>';
                html += '</span>';
            } else {
                html += '<span></span>';
            }
            if (r < outputs.length) {
                html += '<span class="df-port-out">';
                html += outputs[r].label;
                html += ' <span class="df-port-dot" style="color:' + outputs[r].color + '">&#x25CF;</span>';
                html += '</span>';
            }
            html += '</div>';
        }
        html += '</div>';
        return html;
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

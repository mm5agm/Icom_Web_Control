// Yaesu Web Control – WebSocket Connection Manager
// Handles connection, reconnection, message routing, and error handling.
// No UI, no DOM, no gauge logic. Pure networking only.

export class WsConnection {
    constructor(url, pipeline, options = {}) {
        this.url = url;
        this.pipeline = pipeline;

        this.autoReconnect = options.autoReconnect ?? true;
        this.reconnectDelay = options.reconnectDelay ?? 2000;
        this.maxReconnectAttempts = options.maxReconnectAttempts ?? Infinity;

        this._socket = null;
        this._reconnectAttempts = 0;
        this._isClosing = false;
    }

    // Start the WebSocket connection
    connect() {
        this._isClosing = false;
        this._socket = new WebSocket(this.url);

        this._socket.onopen = () => {
            this._reconnectAttempts = 0;
        };

        this._socket.onmessage = (event) => {
            if (this.pipeline && typeof this.pipeline.handleMessage === 'function') {
                this.pipeline.handleMessage(event.data);
            }
        };

        this._socket.onerror = () => {
            // Errors are handled by onclose
        };

        this._socket.onclose = () => {
            if (!this._isClosing && this.autoReconnect) {
                this._attemptReconnect();
            }
        };
    }

    // Gracefully close the connection
    close() {
        this._isClosing = true;
        if (this._socket) {
            this._socket.close();
        }
    }

    // Send data to the server
    send(data) {
        if (this._socket && this._socket.readyState === WebSocket.OPEN) {
            this._socket.send(data);
        }
    }

    // Internal reconnection logic
    _attemptReconnect() {
        if (this._reconnectAttempts >= this.maxReconnectAttempts) {
            return;
        }

        this._reconnectAttempts++;

        setTimeout(() => {
            if (!this._isClosing) {
                this.connect();
            }
        }, this.reconnectDelay);
    }
}

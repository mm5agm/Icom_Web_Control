// Yaesu Web Control – WebSocket Update Pipeline
// Pure routing only. No DOM, no UI, no gauge logic, no calibration.
// Receives { property, value } messages and dispatches to registered handlers.

export class WsUpdatePipeline {
    constructor(options = {}) {
        // Handlers keyed by SignalR property name
        this._handlers = {};

        // Register any handlers supplied at construction time
        if (options.handlers && typeof options.handlers === 'object') {
            for (const [property, fn] of Object.entries(options.handlers)) {
                this.register(property, fn);
            }
        }

        this.onUnknown = options.onUnknown || null;
    }

    /**
     * Register a handler for a specific property name.
     * @param {string}   property  SignalR property name, e.g. 'PowerMeter'
     * @param {Function} handler   Called with (value, property)
     */
    register(property, handler) {
        this._handlers[property] = handler;
    }

    /**
     * Entry point for incoming messages.
     * Accepts either a raw JSON string or a pre-parsed { property, value } object.
     * @param {string|Object} message
     */
    handleMessage(message) {
        const payload = this._normalise(message);
        if (!payload || typeof payload.property !== 'string' || payload.value === undefined) {
            return;
        }

        const handler = this._handlers[payload.property];
        if (handler) {
            handler(payload.value, payload.property);
        } else if (this.onUnknown) {
            this.onUnknown(payload);
        }
    }

    _normalise(message) {
        if (typeof message === 'string') {
            try {
                return JSON.parse(message);
            } catch {
                return null;
            }
        }
        if (typeof message === 'object' && message !== null) {
            return message;
        }
        return null;
    }
}

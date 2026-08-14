/*
  Message transport between the host and this page.

  Both directions use the same envelope: { type, seq, payload }. Inbound messages are
  dispatched to handlers registered by graph.js, which keeps the renderer free of any
  knowledge of how the messages arrive. The receive() entry point is public so the page
  can also be driven from a plain browser during development, where window.chrome.webview
  does not exist.
*/
(function () {
    "use strict";

    var handlers = Object.create(null);
    var host = (window.chrome && window.chrome.webview) || null;
    var outboundSeq = 0;

    function receive(envelope) {
        if (!envelope || typeof envelope.type !== "string") {
            return;
        }

        var handler = handlers[envelope.type];
        if (!handler) {
            return;
        }

        try {
            handler(envelope.payload || {});
        } catch (err) {
            // Report rather than swallow: a handler that throws silently would leave the
            // canvas showing stale facts with no sign that anything went wrong.
            reportError("handling '" + envelope.type + "': " + describe(err));
        }
    }

    function post(type, payload) {
        outboundSeq += 1;
        var envelope = { type: type, seq: outboundSeq, payload: payload || {} };

        if (host) {
            host.postMessage(envelope);
        } else {
            console.log("[bridge] no host, dropping", envelope);
        }
    }

    function describe(err) {
        if (!err) {
            return "unknown error";
        }
        return (err.stack || err.message || String(err)).slice(0, 2000);
    }

    function reportError(message) {
        // Guard against a failure loop if posting is itself what is broken.
        if (host) {
            try {
                host.postMessage({ type: "error", seq: ++outboundSeq, payload: { message: message } });
            } catch (ignored) {
                console.error(message);
            }
        } else {
            console.error(message);
        }
    }

    if (host) {
        host.addEventListener("message", function (event) {
            receive(event.data);
        });
    }

    window.addEventListener("error", function (event) {
        reportError(describe(event.error) || event.message);
    });

    window.addEventListener("unhandledrejection", function (event) {
        reportError("unhandled rejection: " + describe(event.reason));
    });

    // Devtools are off in the shipped app, so a policy violation would otherwise be
    // invisible. Surfacing it to the host log is the only way to notice that a bundled
    // library has started reaching for something the policy forbids.
    document.addEventListener("securitypolicyviolation", function (event) {
        reportError(
            "CSP " + event.violatedDirective + " blocked " +
            (event.blockedURI || "inline") +
            " at " + (event.sourceFile || "?") + ":" + (event.lineNumber || 0) +
            (event.sample ? " sample=" + event.sample : ""));
    });

    window.graphBridge = {
        on: function (type, handler) {
            handlers[type] = handler;
        },
        post: post,
        receive: receive,
        reportError: reportError,
        hasHost: Boolean(host)
    };
})();

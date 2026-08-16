/*
  View switching.

  Five very different pictures share this page and one message bridge. Each view module
  registers itself here with the section it owns; this file does nothing but show one and
  hide the rest, then tell the newcomer it is visible.

  That last part matters more than it looks: cytoscape and d3 both size themselves from
  their container, and a container that was display:none measures zero. Anything laid out
  while hidden comes back as a dot in the corner, so every view gets an onShow hook and
  re-measures there.

  Loading data is the host's job, not this file's. The host sends setViewMode and then the
  payload for that view, so the page never has to ask for anything.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;

    var views = Object.create(null);
    var order = [];
    var current = null;

    function register(name, view) {
        views[name] = view;
        order.push(name);

        if (view.element) {
            view.element.hidden = name !== current;
        }
    }

    function show(name) {
        if (!views[name] || name === current) {
            return;
        }

        order.forEach(function (other) {
            var view = views[other];
            if (!view.element) {
                return;
            }

            var visible = other === name;
            view.element.hidden = !visible;

            if (!visible && typeof view.onHide === "function") {
                view.onHide();
            }
        });

        current = name;

        var active = views[name];
        if (typeof active.onShow === "function") {
            active.onShow();
        }
    }

    function forEachView(callback) {
        order.forEach(function (name) {
            callback(views[name], name);
        });
    }

    // The graph section starts visible in the markup, so start in that state rather than
    // flipping it on the first frame.
    current = "graph";

    bridge.on("setViewMode", function (payload) {
        show(payload.mode || "graph");
    });

    /*
      One export handler for the whole page. bridge.on keys handlers by message type and
      the last registration silently wins, so two views must never both claim
      "exportView" — each exposes an onExport hook instead, and the request goes to
      whichever view is visible. A view without the hook answers data:null, which the
      host already reads as "nothing to export".
    */
    bridge.on("exportView", function (payload) {
        var format = (payload && payload.format) || "png";
        var active = current ? views[current] : null;

        if (active && typeof active.onExport === "function") {
            active.onExport(format);
            return;
        }

        bridge.post("exportResult", { format: format, data: null });
    });

    bridge.on("setTheme", function (payload) {
        var theme = payload.theme === "light" ? "light" : "dark";
        document.documentElement.setAttribute("data-theme", theme);

        // Colours are read from CSS custom properties at draw time, so every view has to
        // be told to redraw; a hidden one still redraws, because it may be shown before
        // the next data message arrives.
        forEachView(function (view) {
            if (typeof view.onTheme === "function") {
                view.onTheme(theme);
            }
        });
    });

    window.addEventListener("resize", function () {
        var active = current ? views[current] : null;
        if (active && typeof active.onResize === "function") {
            active.onResize();
        }
    });

    // Every view module is loaded by the time the document is parsed, so this is the first
    // moment at which the host can safely send anything.
    document.addEventListener("DOMContentLoaded", function () {
        bridge.post("ready", {});
    });

    window.viewHost = {
        register: register,
        show: show,
        current: function () {
            return current;
        }
    };

    // ---- Shared helpers ----------------------------------------------------

    function cssVar(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    }

    /*
      textContent, never innerHTML. Every string these views render — symbol names, type
      text, literal initialisers, file paths — is verbatim source from the workspace, and
      source is full of angle brackets.
    */
    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null && text !== "") {
            node.textContent = String(text);
        }
        return node;
    }

    function clear(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }

    function count(value, singular, pluralForm) {
        return value + " " + (value === 1 ? singular : pluralForm);
    }

    /*
      Shows or hides an overlay, replacing its text.

      The paragraph is looked up rather than reached through firstChild: the markup is
      indented, so firstChild is a whitespace text node and writing to it silently leaves
      the visible message untouched.
    */
    function overlay(element, message) {
        if (message) {
            element.querySelector("p").textContent = message;
        }
        element.hidden = !message;
    }

    function truncate(text, limit) {
        if (!text) {
            return "";
        }
        return text.length > limit ? text.slice(0, limit - 1) + "…" : text;
    }

    window.viewUtil = {
        cssVar: cssVar,
        el: el,
        clear: clear,
        count: count,
        overlay: overlay,
        truncate: truncate,
        groupColour: function (group) {
            return cssVar("--kind-" + (group || "variable"));
        }
    };
})();

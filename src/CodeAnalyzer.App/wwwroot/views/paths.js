/*
  Path tracer.

  How one symbol reaches another, drawn as a left-to-right flow. Its own cytoscape
  instance rather than a mode of the neighbourhood graph: the two answer different
  questions and switching between them should not cost the layout you were reading.

  The important honesty here is in the empty case. "No route" and "we stopped looking"
  look identical on screen unless the view says which one happened, so the host sends a
  flag for it and this view prints the difference.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var SHAPES = {
        function: "round-rectangle",
        type: "hexagon",
        constant: "diamond",
        macro: "tag",
        module: "rectangle",
        variable: "ellipse"
    };

    var GROUPS = ["function", "type", "constant", "macro", "module", "variable"];

    var elements = {
        section: document.getElementById("view-paths"),
        canvas: document.getElementById("paths-canvas"),
        note: document.getElementById("paths-note"),
        routes: document.getElementById("paths-routes"),
        empty: document.getElementById("paths-empty")
    };

    var lastPayload = null;

    var cy = cytoscape({
        container: elements.canvas,
        style: buildStyle(),
        minZoom: 0.1,
        maxZoom: 3
    });

    function buildStyle() {
        var style = [
            {
                selector: "node",
                style: {
                    label: "data(name)",
                    "text-valign": "center",
                    "text-halign": "center",
                    "font-family": util.cssVar("--font-mono"),
                    "font-size": 11,
                    color: util.cssVar("--node-text"),
                    "text-max-width": 170,
                    width: "label",
                    height: "label",
                    padding: 9,
                    shape: "round-rectangle",
                    "background-opacity": 0.18,
                    "border-width": 1.5
                }
            },
            {
                /* The two endpoints the user actually chose. */
                selector: "node[?isEndpoint]",
                style: {
                    "border-width": 3,
                    "border-color": util.cssVar("--accent"),
                    "background-opacity": 0.32,
                    "font-weight": "bold"
                }
            },
            {
                selector: "edge",
                style: {
                    width: 1.8,
                    "curve-style": "bezier",
                    "target-arrow-shape": "triangle",
                    "arrow-scale": 0.85,
                    "line-color": util.cssVar("--edge"),
                    "target-arrow-color": util.cssVar("--edge"),
                    label: "data(kind)",
                    "font-size": 9,
                    "text-rotation": "autorotate",
                    "text-background-color": util.cssVar("--panel"),
                    "text-background-opacity": 0.85,
                    "text-background-padding": 2,
                    color: util.cssVar("--fg-muted"),
                    opacity: 0.9
                }
            },
            {
                selector: 'edge[confidence = "ambiguous"]',
                style: { "line-style": "dashed", "line-dash-pattern": [6, 3] }
            },
            {
                selector: 'edge[confidence = "weak"]',
                style: {
                    "line-style": "dotted",
                    "line-color": util.cssVar("--edge-weak"),
                    "target-arrow-color": util.cssVar("--edge-weak")
                }
            },
            {
                /* Everything not on the route being hovered. */
                selector: ".dimmed",
                style: { opacity: 0.15 }
            },
            {
                selector: ".on-route",
                style: {
                    width: 3,
                    "line-color": util.cssVar("--accent"),
                    "target-arrow-color": util.cssVar("--accent"),
                    opacity: 1
                }
            }
        ];

        GROUPS.forEach(function (group) {
            var colour = util.cssVar("--kind-" + group);
            style.push({
                selector: 'node[group = "' + group + '"]',
                style: {
                    shape: SHAPES[group],
                    "background-color": colour,
                    "border-color": colour
                }
            });
        });

        return style;
    }

    // ---- Rendering ---------------------------------------------------------

    function setEmpty(message) {
        util.overlay(elements.empty, message);
    }

    function setNote(text, warning) {
        if (!text) {
            elements.note.hidden = true;
            return;
        }

        elements.note.textContent = text;
        elements.note.classList.toggle("notice-warn", Boolean(warning));
        elements.note.hidden = false;
    }

    function describeEmptyResult(payload) {
        if (payload.missingEndpoint) {
            return "One of the two symbols is no longer in the index. Re-index and try again.";
        }

        if (payload.exhausted) {
            // The distinction the whole flag exists for.
            return "No route found within the search limit. There may still be a longer one — " +
                "the search stopped before it could rule that out.";
        }

        return "Nothing links " + (payload.fromName || "the start") +
            " to " + (payload.toName || "the end") +
            " through the references indexed here.";
    }

    function toElements(payload) {
        var result = [];

        (payload.nodes || []).forEach(function (node) {
            result.push({
                group: "nodes",
                data: {
                    id: node.id,
                    name: node.name,
                    kind: node.kind,
                    group: node.group,
                    path: node.path,
                    line: node.line,
                    step: node.step,
                    isEndpoint: node.id === payload.fromId || node.id === payload.toId
                }
            });
        });

        (payload.links || []).forEach(function (link) {
            result.push({
                group: "edges",
                data: {
                    id: link.id,
                    source: link.source,
                    target: link.target,
                    kind: link.kind,
                    confidence: link.confidence,
                    confidenceLabel: link.confidenceLabel,
                    line: link.line
                }
            });
        });

        return result;
    }

    function routeList(payload) {
        util.clear(elements.routes);

        if (!payload.routes || payload.routes.length === 0) {
            elements.routes.hidden = true;
            return;
        }

        var names = Object.create(null);
        (payload.nodes || []).forEach(function (node) {
            names[node.id] = node.name;
        });

        var heading = el("div", "route-heading",
            util.count(payload.routes.length, "route", "routes") +
            ", " + util.count(payload.length, "hop", "hops") + " each");
        elements.routes.appendChild(heading);

        payload.routes.forEach(function (route, index) {
            var row = el("button", "route");
            row.type = "button";
            row.dataset.index = index;

            route.nodes.forEach(function (id, position) {
                if (position > 0) {
                    row.appendChild(el("span", "route-arrow", "→"));
                }
                row.appendChild(el("span", "route-step", names[id] || id));
            });

            if (route.confidence !== "unique") {
                // The route inherits the doubt of its weakest hop, so say so on the route
                // rather than making the reader find which edge was dashed.
                row.appendChild(el("span", "warn",
                    route.confidence === "weak" ? "cross-language hop" : "ambiguous hop"));
            }

            elements.routes.appendChild(row);
        });

        elements.routes.hidden = false;
    }

    function highlightRoute(index) {
        clearHighlight();

        if (index === null || !lastPayload || !lastPayload.routes[index]) {
            return;
        }

        var ids = lastPayload.routes[index].nodes;
        var keep = cy.collection();

        ids.forEach(function (id, position) {
            keep = keep.union(cy.getElementById(id));
            if (position > 0) {
                var edge = cy.edges('[source = "' + ids[position - 1] + '"][target = "' + id + '"]');
                keep = keep.union(edge);
                edge.addClass("on-route");
            }
        });

        cy.elements().difference(keep).addClass("dimmed");
    }

    function clearHighlight() {
        cy.elements().removeClass("dimmed").removeClass("on-route");
    }

    function render(payload) {
        lastPayload = payload;

        cy.elements().remove();

        if (!payload) {
            setNote(null);
            elements.routes.hidden = true;
            setEmpty("Pick a start and an end symbol to trace how one reaches the other.");
            return;
        }

        if (!payload.routes || payload.routes.length === 0) {
            setNote(null);
            elements.routes.hidden = true;
            setEmpty(describeEmptyResult(payload));
            return;
        }

        setEmpty(null);
        cy.add(toElements(payload));
        routeList(payload);

        setNote(
            payload.truncated
                ? "Showing the first " + payload.routes.length + " shortest routes; there are more."
                : null,
            true);

        runLayout();
    }

    function runLayout() {
        if (cy.nodes().length === 0) {
            return;
        }

        cy.layout({
            name: "dagre",
            rankDir: "LR",
            nodeSep: 20,
            rankSep: 90,
            edgeSep: 10,
            padding: 40,
            animate: cy.nodes().length <= 120,
            animationDuration: 240,
            nodeDimensionsIncludeLabels: true
        }).run();
    }

    // ---- Interaction -------------------------------------------------------

    elements.routes.addEventListener("mouseover", function (event) {
        var row = event.target.closest(".route");
        if (row) {
            highlightRoute(Number(row.dataset.index));
        }
    });

    elements.routes.addEventListener("mouseleave", clearHighlight);

    cy.on("tap", "node", function (event) {
        bridge.post("nodeSelected", { id: event.target.id() });
    });

    cy.on("tap", function (event) {
        if (event.target === cy) {
            clearHighlight();
        }
    });

    // ---- Host messages -----------------------------------------------------

    bridge.on("setPaths", function (payload) {
        render(payload.paths || null);
    });

    window.viewHost.register("paths", {
        element: elements.section,
        onShow: function () {
            cy.resize();
            if (cy.nodes().length > 0) {
                cy.fit(undefined, 40);
            }
        },
        onResize: function () {
            cy.resize();
        },
        onTheme: function () {
            cy.style().fromJson(buildStyle()).update();
        }
    });
})();

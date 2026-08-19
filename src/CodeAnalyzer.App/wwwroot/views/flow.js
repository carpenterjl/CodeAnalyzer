/*
  Call flow.

  Every call one function makes, in source order, transitively, with where each result
  went. A step is a call SITE, not a function: repeats appear again, and the second
  occurrence of a fully drawn subtree collapses to a reference pointing at the drawing.

  One store, several renderers. The trace model and the ui state (layout, collapsed
  ordinals, selection, breadcrumb trail) live here; each layout is a render function over
  them, so switching layouts never loses where the reader was. The indented tree is the
  default; the other layouts register themselves in RENDERERS as they are built.

  Honesty rules carried from the engine: source order is not execution order (the empty
  overlay says so), a cut is always labelled with what the body actually holds, doubt is
  a visible badge that opens the candidate list, and an unresolved call is a grey leaf,
  never a missing row.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-flow"),
        layouts: document.getElementById("flow-layouts"),
        depthDown: document.getElementById("flow-depth-down"),
        depthUp: document.getElementById("flow-depth-up"),
        depthValue: document.getElementById("flow-depth-value"),
        crumbs: document.getElementById("flow-crumbs"),
        note: document.getElementById("flow-note"),
        tree: document.getElementById("flow-tree"),
        canvas: document.getElementById("flow-canvas"),
        seq: document.getElementById("flow-seq"),
        flame: document.getElementById("flow-flame"),
        rankdir: document.getElementById("flow-rankdir"),
        tip: document.getElementById("flow-tip"),
        picker: document.getElementById("flow-picker"),
        empty: document.getElementById("flow-empty")
    };

    var model = null;                       // the last setFlow payload
    var byOrdinal = Object.create(null);    // ordinal -> step, with __parent links
    var ui = {
        layout: "tree",
        collapsed: Object.create(null),     // ordinal -> true, user-folded subtrees
        selection: null,                    // ordinal
        depth: 3,
        rankDir: "TB",                      // flowchart only
        crumbs: [],                         // previous roots [{ id, name }]
        pendingRoot: null                   // a crumb click in flight: do not re-push
    };

    /* Renderers by layout id. Flowchart, sequence and flame register here as they
       arrive; the picker is built from this table so it can never offer a dead button. */
    var RENDERERS = {
        tree: renderTree,
        flowchart: renderFlowchart,
        sequence: renderSequence,
        flame: renderFlame
    };
    var PANES = { tree: "tree", flowchart: "canvas", sequence: "seq", flame: "flame" };
    var LAYOUT_LABELS = { tree: "Tree", flowchart: "Flowchart", sequence: "Sequence", flame: "Flame" };
    var LAYOUT_ORDER = ["tree", "flowchart", "sequence", "flame"];

    // ---- Model indexing ----------------------------------------------------

    function indexSteps(steps, parent) {
        steps.forEach(function (step) {
            step.__parent = parent;
            byOrdinal[step.ordinal] = step;
            if (step.steps && step.steps.length) {
                indexSteps(step.steps, step);
            }
        });
    }

    function reindex() {
        byOrdinal = Object.create(null);
        if (model) {
            indexSteps(model.steps, null);
        }
    }

    /* The target ids of every enclosing occurrence, root included — what a deepen
       request sends so the grafted branch cannot re-expand its own callers. */
    function ancestorIdsOf(step) {
        var ids = [];
        if (model && model.root) {
            ids.push(model.root.id);
        }
        for (var walk = step.__parent; walk; walk = walk.__parent) {
            if (walk.target) {
                ids.push(walk.target.id);
            }
        }
        return ids;
    }

    function callText(step) {
        return (step.isNew ? "new " : "")
            + (step.receiver ? step.receiver + "." : "")
            + step.name
            + (step.args || "");
    }

    function fateText(step) {
        switch (step.fate) {
            case "assigned": return "→ " + (step.fateName || "assigned");
            case "discarded": return "→ (discarded)";
            case "returned": return "→ returned";
            case "arg": return "→ passed on";
            case "tested": return "→ tested";
            default: return "";
        }
    }

    // ---- Host messages -----------------------------------------------------

    bridge.on("setFlow", function (payload) {
        var flow = payload.flow || null;

        if (flow && model && model.root && flow.root && flow.root.id !== model.root.id) {
            if (ui.pendingRoot === flow.root.id) {
                // A breadcrumb click: the trail was already cut, going back is not a step.
                ui.pendingRoot = null;
            } else {
                ui.crumbs.push({ id: model.root.id, name: model.root.name || ("#" + model.root.id) });
            }
            ui.collapsed = Object.create(null);
            ui.selection = null;
        }

        model = flow;
        if (flow) {
            ui.depth = flow.depth || ui.depth;
        }
        reindex();
        hideTip();
        hidePicker();
        render();
    });

    bridge.on("flowBranch", function (payload) {
        var branch = payload.branch;
        if (!branch || !model) {
            return;
        }

        var at = byOrdinal[branch.at];
        if (!at) {
            return;
        }

        at.steps = branch.steps || [];
        at.truncated = null;
        reindex();
        render();
    });

    // ---- Toolbar -----------------------------------------------------------

    function buildLayoutButtons() {
        util.clear(elements.layouts);
        LAYOUT_ORDER.forEach(function (id) {
            if (!RENDERERS[id]) {
                return;
            }

            var button = el("button", null, LAYOUT_LABELS[id]);
            button.setAttribute("aria-pressed", String(ui.layout === id));
            button.addEventListener("click", function () {
                if (ui.layout !== id) {
                    ui.layout = id;
                    buildLayoutButtons();
                    render();
                }
            });
            elements.layouts.appendChild(button);
        });

        // A picker with one entry is noise, not a choice.
        elements.layouts.hidden = Object.keys(RENDERERS).length < 2;
    }

    function stepDepth(delta) {
        var next = Math.min(10, Math.max(1, ui.depth + delta));
        if (next === ui.depth) {
            return;
        }

        ui.depth = next;
        elements.depthValue.textContent = String(next);
        bridge.post("flowDepth", { depth: next });
    }

    elements.depthDown.addEventListener("click", function () { stepDepth(-1); });
    elements.depthUp.addEventListener("click", function () { stepDepth(1); });

    elements.rankdir.addEventListener("click", function () {
        ui.rankDir = ui.rankDir === "TB" ? "LR" : "TB";
        elements.rankdir.textContent = ui.rankDir;
        if (ui.layout === "flowchart") {
            render();
        }
    });

    function renderCrumbs() {
        util.clear(elements.crumbs);
        ui.crumbs.forEach(function (crumb, index) {
            var link = el("button", "crumb", crumb.name);
            link.addEventListener("click", function () {
                ui.pendingRoot = crumb.id;
                ui.crumbs = ui.crumbs.slice(0, index);
                bridge.post("flowRoot", { id: crumb.id });
            });
            elements.crumbs.appendChild(link);
        });

        if (model && model.root) {
            elements.crumbs.appendChild(
                el("span", "crumb-current", model.root.name || ("#" + model.root.id)));
        }
    }

    // ---- Rendering ---------------------------------------------------------

    function render() {
        var hasFlow = !!(model && model.root);
        elements.empty.hidden = hasFlow;
        elements.depthValue.textContent = String(ui.depth);
        elements.rankdir.hidden = ui.layout !== "flowchart";
        renderCrumbs();

        Object.keys(PANES).forEach(function (layout) {
            elements[PANES[layout]].hidden = layout !== ui.layout;
        });

        if (!hasFlow) {
            util.clear(elements.tree);
            elements.note.hidden = true;
            return;
        }

        elements.note.hidden = !model.truncated;
        if (model.truncated) {
            elements.note.textContent =
                "Cut by a depth or budget cap — every cut step says what its body holds.";
        }

        (RENDERERS[ui.layout] || renderTree)();
    }

    function renderTree() {
        util.clear(elements.tree);
        model.steps.forEach(function (step) {
            elements.tree.appendChild(renderStep(step));
        });

        if (model.rootTruncated) {
            elements.tree.appendChild(el(
                "div", "flow-more",
                "… showing " + model.rootTruncated.shown + " of " + model.rootTruncated.total
                    + " calls in " + (model.root.name || "the root")));
        }
    }

    function renderStep(step) {
        var container = el("div", "flow-step");
        var hasChildren = step.steps && step.steps.length > 0;
        var folded = !!ui.collapsed[step.ordinal];

        var row = el("div", "flow-step-row");
        if (step.unresolved) {
            row.classList.add("flow-unresolved");
        }
        if (step.collapsedAt) {
            row.classList.add("flow-ref-row");
        }
        if (ui.selection === step.ordinal) {
            row.classList.add("selected");
        }
        row.dataset.ordinal = step.ordinal;

        var caret = el("span", "flow-caret", hasChildren ? (folded ? "▸" : "▾") : "");
        if (hasChildren) {
            caret.addEventListener("click", function (event) {
                event.stopPropagation();
                if (ui.collapsed[step.ordinal]) {
                    delete ui.collapsed[step.ordinal];
                } else {
                    ui.collapsed[step.ordinal] = true;
                }
                render();
            });
        }
        row.appendChild(caret);

        row.appendChild(el("span", "flow-ordinal", step.ordinal + " :" + step.line));

        var call = el("span", "flow-call");
        if (step.isNew) {
            call.appendChild(el("span", "flow-new", "new "));
        }
        call.appendChild(document.createTextNode(
            (step.isNew ? "" : "") + (step.receiver ? step.receiver + "." : "")));
        var nameSpan = el("span", null, step.name);
        if (step.target) {
            nameSpan.style.textDecoration = "underline dotted";
            nameSpan.addEventListener("click", function (event) {
                event.stopPropagation();
                bridge.post("nodeSelected", { id: step.target.id });
            });
        }
        call.appendChild(nameSpan);
        call.appendChild(document.createTextNode(util.truncate(step.args || "", 90)));
        row.appendChild(call);

        var fate = fateText(step);
        if (fate) {
            row.appendChild(el("span", "flow-fate", fate));
        }

        if (step.collapsedAt) {
            var reference = el("button", "flow-badge", "↑ expanded at " + step.collapsedAt);
            reference.addEventListener("click", function (event) {
                event.stopPropagation();
                revealOrdinal(step.collapsedAt);
            });
            row.appendChild(reference);
        } else if (step.cycle) {
            var cycle = el("span", "flow-badge cycle", "↺ recursion");
            if (step.cycleOf) {
                cycle.title = "re-enters the occurrence at " + step.cycleOf;
            }
            row.appendChild(cycle);
        } else if (step.unresolved) {
            row.appendChild(el("span", "flow-badge", "unresolved"));
        }

        if (step.candidates && step.candidates.length > 0) {
            var doubt = el("button", "flow-badge", "~ 1 of " + (step.candidates.length + 1));
            doubt.title = step.confidenceLabel || "one of several name matches";
            doubt.addEventListener("click", function (event) {
                event.stopPropagation();
                showPicker(step, event);
            });
            row.appendChild(doubt);
        }

        if (step.io) {
            var chip = el("span", "flow-io-chip");
            chip.appendChild(el("span", null,
                step.io.direction + (step.io.family ? " — " + step.io.family : "")));
            row.appendChild(chip);
        }

        if (step.target && !step.collapsedAt) {
            row.appendChild(el("span", "flow-where",
                (step.target.path || "") + ":" + step.target.line));
        }

        row.addEventListener("click", function () {
            selectStep(step);
        });
        row.addEventListener("dblclick", function () {
            if (step.target) {
                bridge.post("flowRoot", { id: step.target.id });
            }
        });
        row.addEventListener("mousemove", function (event) {
            showTip(step, event);
        });
        row.addEventListener("mouseleave", hideTip);

        container.appendChild(row);

        if (hasChildren && !folded) {
            var children = el("div", "flow-children");
            step.steps.forEach(function (child) {
                children.appendChild(renderStep(child));
            });
            container.appendChild(children);
        }

        if (step.truncated && step.truncated.total > 0 && !folded) {
            var more = el("div", "flow-more",
                (step.truncated.shown > 0
                    ? "… showing " + step.truncated.shown + " of " + step.truncated.total + " calls"
                    : "+ " + step.truncated.total + " call(s) inside")
                + " — click to expand");
            more.addEventListener("click", function () {
                if (step.target) {
                    bridge.post("flowDeepen", {
                        targetId: step.target.id,
                        ordinal: step.ordinal,
                        ancestors: ancestorIdsOf(step)
                    });
                }
            });
            container.appendChild(more);
        }

        return container;
    }

    function selectStep(step) {
        ui.selection = step.ordinal;
        var previous = elements.tree.querySelector(".flow-step-row.selected");
        if (previous) {
            previous.classList.remove("selected");
        }
        var row = elements.tree.querySelector('[data-ordinal="' + step.ordinal + '"]');
        if (row) {
            row.classList.add("selected");
        }

        // The existing round-trip: the shell opens the caller's file at the call site.
        bridge.post("edgeActivated", { source: step.callerId, line: step.line });
    }

    /* Unfolds whatever hides an ordinal, then scrolls to it and flashes it. */
    function revealOrdinal(ordinal) {
        var step = byOrdinal[ordinal];
        if (!step) {
            return;
        }

        var changed = false;
        for (var walk = step.__parent; walk; walk = walk.__parent) {
            if (ui.collapsed[walk.ordinal]) {
                delete ui.collapsed[walk.ordinal];
                changed = true;
            }
        }
        if (changed) {
            render();
        }

        var row = elements.tree.querySelector('[data-ordinal="' + ordinal + '"]');
        if (row) {
            row.scrollIntoView({ block: "center" });
            row.classList.remove("flash");
            void row.offsetWidth; // restart the animation
            row.classList.add("flash");
        }
    }

    // ---- Hover card --------------------------------------------------------

    function showTip(step, event) {
        var tip = elements.tip;
        util.clear(tip);
        tip.appendChild(el("div", "tip-title", callText(step)));

        var fate = fateText(step);
        if (fate) {
            tip.appendChild(el("div", "tip-line", "result " + fate));
        }
        if (step.confidenceLabel && step.confidence !== "unique") {
            tip.appendChild(el("div", "tip-line", step.confidenceLabel));
        }
        if (step.unresolved) {
            tip.appendChild(el("div", "tip-line", "resolved to nothing in this index"));
        }
        if (step.target) {
            tip.appendChild(el("div", "tip-line",
                (step.target.name || step.name) + " · " + (step.target.kind || "")
                + " · " + (step.target.path || "") + ":" + step.target.line));
        }
        if (step.io) {
            tip.appendChild(el("div", "tip-line",
                "I/O boundary: " + step.io.direction
                + (step.io.family ? " — " + step.io.family : "")));
        }
        tip.appendChild(el("div", "tip-hint",
            "click: open call site · double-click: trace from here"));

        tip.hidden = false;
        positionTip(event);
    }

    function positionTip(event) {
        var bounds = elements.section.getBoundingClientRect();
        var left = event.clientX - bounds.left + 14;
        var top = event.clientY - bounds.top + 12;

        if (left + elements.tip.offsetWidth > bounds.width - 8) {
            left = event.clientX - bounds.left - elements.tip.offsetWidth - 10;
        }
        if (top + elements.tip.offsetHeight > bounds.height - 8) {
            top = event.clientY - bounds.top - elements.tip.offsetHeight - 10;
        }

        elements.tip.style.left = Math.max(4, left) + "px";
        elements.tip.style.top = Math.max(4, top) + "px";
    }

    function hideTip() {
        elements.tip.hidden = true;
    }

    // ---- Candidate picker --------------------------------------------------

    function showPicker(step, event) {
        var picker = elements.picker;
        util.clear(picker);

        picker.appendChild(el("div", "context-menu-title",
            "one name, " + (step.candidates.length + 1) + " definitions — pick the one to follow"));

        function item(id, name, where, isCurrent) {
            var button = el("button", null, name + "  " + where + (isCurrent ? "  ✓" : ""));
            button.addEventListener("click", function () {
                hidePicker();
                if (!isCurrent) {
                    bridge.post("flowPin", { refId: step.refId, candidateId: id });
                }
            });
            picker.appendChild(button);
        }

        if (step.target) {
            item(step.target.id, step.target.name || step.name,
                (step.target.path || "") + ":" + step.target.line, true);
        }
        step.candidates.forEach(function (candidate) {
            item(candidate.id, candidate.name, candidate.path + ":" + candidate.line, false);
        });

        picker.hidden = false;
        positionPicker(event);
    }

    function positionPicker(event) {
        var bounds = elements.section.getBoundingClientRect();
        var left = event.clientX - bounds.left;
        var top = event.clientY - bounds.top + 10;

        if (left + elements.picker.offsetWidth > bounds.width - 8) {
            left = bounds.width - elements.picker.offsetWidth - 8;
        }

        elements.picker.style.left = Math.max(4, left) + "px";
        elements.picker.style.top = Math.max(4, top) + "px";
    }

    function hidePicker() {
        elements.picker.hidden = true;
    }

    document.addEventListener("click", function (event) {
        if (!elements.picker.hidden && !elements.picker.contains(event.target)) {
            hidePicker();
        }
    });

    // ---- Flowchart (cytoscape + dagre) -------------------------------------

    var cy = null;

    function flowchartStyle() {
        return [
            {
                selector: "node",
                style: {
                    label: "data(label)",
                    "text-valign": "center",
                    "text-halign": "center",
                    "font-family": util.cssVar("--font-mono"),
                    "font-size": 10,
                    color: util.cssVar("--node-text"),
                    "text-wrap": "wrap",
                    "text-max-width": 190,
                    width: "label",
                    height: "label",
                    padding: 8,
                    shape: "round-rectangle",
                    "background-color": "data(colour)",
                    "background-opacity": 0.18,
                    "border-width": 1.5,
                    "border-color": "data(colour)"
                }
            },
            {
                selector: "node.root",
                style: { "border-width": 3, "border-color": util.cssVar("--accent"), "font-weight": "bold" }
            },
            {
                selector: "node.io",
                style: { shape: "rhomboid" }
            },
            {
                selector: "node.unresolved",
                style: { "background-opacity": 0.06, color: util.cssVar("--fg-faint"), "border-style": "dotted" }
            },
            {
                selector: "node.cut",
                style: {
                    shape: "round-rectangle",
                    "background-opacity": 0,
                    "border-style": "dashed",
                    color: util.cssVar("--fg-muted"),
                    "font-size": 9
                }
            },
            {
                selector: "edge",
                style: {
                    width: 1.6,
                    "curve-style": "bezier",
                    "target-arrow-shape": "triangle",
                    "arrow-scale": 0.8,
                    "line-color": util.cssVar("--edge"),
                    "target-arrow-color": util.cssVar("--edge"),
                    label: "data(label)",
                    "font-size": 8.5,
                    color: util.cssVar("--fg-muted"),
                    "text-rotation": "autorotate",
                    "text-background-color": util.cssVar("--panel"),
                    "text-background-opacity": 0.85,
                    "text-background-padding": 2
                }
            },
            { selector: "edge.ambiguous", style: { "line-style": "dashed" } },
            {
                selector: "edge.weak",
                style: {
                    "line-style": "dotted",
                    "line-color": util.cssVar("--edge-weak"),
                    "target-arrow-color": util.cssVar("--edge-weak")
                }
            },
            {
                selector: "edge.back",
                style: {
                    "line-style": "dashed",
                    width: 1.1,
                    "line-color": util.cssVar("--fg-faint"),
                    "target-arrow-color": util.cssVar("--fg-faint"),
                    "arrow-scale": 0.6
                }
            },
            {
                selector: "edge.cycle",
                style: {
                    "line-style": "dashed",
                    "line-color": util.cssVar("--warning"),
                    "target-arrow-color": util.cssVar("--warning")
                }
            }
        ];
    }

    function ensureCy() {
        if (cy) {
            return cy;
        }

        cy = cytoscape({
            container: elements.canvas,
            style: flowchartStyle(),
            minZoom: 0.1,
            maxZoom: 3
        });

        cy.on("tap", "node", function (event) {
            var step = event.target.data("step");
            if (step) {
                selectStep(step);
            }
        });
        cy.on("dbltap", "node", function (event) {
            var step = event.target.data("step");
            if (step && step.target) {
                bridge.post("flowRoot", { id: step.target.id });
            }
        });
        cy.on("mouseover", "node", function (event) {
            var step = event.target.data("step");
            if (step && event.originalEvent) {
                showTip(step, event.originalEvent);
            }
        });
        cy.on("mouseout", "node", hideTip);

        return cy;
    }

    /* Walks the steps the current fold state leaves visible. */
    function visibleSteps(steps, visit) {
        steps.forEach(function (step) {
            visit(step);
            if (step.steps && step.steps.length && !ui.collapsed[step.ordinal]) {
                visibleSteps(step.steps, visit);
            }
        });
    }

    function nodeIdFor(ordinal) {
        return "s" + ordinal;
    }

    function parentIdFor(ordinal) {
        var dot = ordinal.lastIndexOf(".");
        return dot < 0 ? "root" : "s" + ordinal.slice(0, dot);
    }

    function renderFlowchart() {
        var graph = ensureCy();
        var nodes = [];
        var edges = [];
        var present = Object.create(null);
        present.root = true;

        nodes.push({
            data: {
                id: "root",
                label: (model.root.name || "?") + " · " + (model.root.kind || ""),
                colour: util.groupColour(model.root.group)
            },
            classes: "root"
        });

        visibleSteps(model.steps, function (step) {
            var id = nodeIdFor(step.ordinal);
            present[id] = true;

            var classes = [];
            if (step.io) { classes.push("io"); }
            if (step.unresolved) { classes.push("unresolved"); }

            nodes.push({
                data: {
                    id: id,
                    label: step.ordinal + " " + util.truncate(callText(step), 48),
                    colour: step.io
                        ? util.cssVar("--kind-io")
                        : util.groupColour(step.target ? step.target.group : "variable"),
                    step: step
                },
                classes: classes.join(" ")
            });

            if (step.truncated && step.truncated.total > 0 && !ui.collapsed[step.ordinal]) {
                nodes.push({
                    data: { id: id + "_cut", label: "… " + step.truncated.total + " more" },
                    classes: "cut"
                });
                edges.push({
                    data: { id: id + "_cutE", source: id, target: id + "_cut", label: "" },
                    classes: "back"
                });
            }
        });

        visibleSteps(model.steps, function (step) {
            var id = nodeIdFor(step.ordinal);
            var parent = parentIdFor(step.ordinal);
            if (!present[parent]) {
                return;
            }

            var callClass = step.unresolved || step.confidence === "ambiguous"
                ? "ambiguous"
                : step.confidence === "weak" ? "weak" : "";
            var callLabel = step.unresolved
                ? "unresolved"
                : step.candidates && step.candidates.length
                    ? "1 of " + (step.candidates.length + 1)
                    : "";
            edges.push({
                data: { id: id + "_call", source: parent, target: id, label: callLabel },
                classes: callClass
            });

            var fate = fateText(step);
            if (fate && step.fate !== "discarded") {
                edges.push({
                    data: { id: id + "_ret", source: id, target: parent, label: fate },
                    classes: "back"
                });
            }

            if (step.cycle) {
                var loopTarget = step.cycleOf === "root" || !step.cycleOf
                    ? "root"
                    : nodeIdFor(step.cycleOf);
                if (present[loopTarget]) {
                    edges.push({
                        data: { id: id + "_loop", source: id, target: loopTarget, label: "↺ recursion" },
                        classes: "cycle"
                    });
                }
            }

            if (step.collapsedAt && present[nodeIdFor(step.collapsedAt)]) {
                edges.push({
                    data: {
                        id: id + "_ref",
                        source: id,
                        target: nodeIdFor(step.collapsedAt),
                        label: "= subtree at " + step.collapsedAt
                    },
                    classes: "back"
                });
            }
        });

        graph.elements().remove();
        graph.add(nodes.concat(edges));
        graph.style().fromJson(flowchartStyle()).update();
        graph.resize();
        graph.layout({
            name: "dagre",
            rankDir: ui.rankDir,
            nodeSep: 26,
            rankSep: 58,
            animate: nodes.length <= 120,
            animationDuration: 300
        }).run();
        graph.fit(undefined, 30);
    }

    // ---- Sequence diagram ---------------------------------------------------

    var SVG_NS = "http://www.w3.org/2000/svg";

    function svgEl(tag, attrs) {
        var node = document.createElementNS(SVG_NS, tag);
        Object.keys(attrs || {}).forEach(function (key) {
            node.setAttribute(key, attrs[key]);
        });
        return node;
    }

    function laneKeyFor(step) {
        return step.target ? "t" + step.target.id : "u" + step.name;
    }

    function renderSequence() {
        util.clear(elements.seq);

        var lanes = [];
        var laneByKey = Object.create(null);

        function lane(key, label) {
            if (!laneByKey[key]) {
                laneByKey[key] = { key: key, label: label, index: lanes.length };
                lanes.push(laneByKey[key]);
            }
            return laneByKey[key];
        }

        lane("t" + model.root.id, model.root.name || ("#" + model.root.id));
        visibleSteps(model.steps, function (step) {
            lane(laneKeyFor(step), (step.target && step.target.name) || step.name);
        });

        // One lifeline per distinct function: a repeat is a repeated arrow, which is what
        // a sequence diagram means by a repeat.
        var events = [];
        var time = 0;
        var bars = [];

        function walk(steps, callerKey) {
            steps.forEach(function (step) {
                var key = laneKeyFor(step);
                var callTime = ++time;
                events.push({
                    kind: "call", time: callTime,
                    from: callerKey, to: key,
                    label: util.truncate(callText(step), 42),
                    step: step
                });

                if (step.steps && step.steps.length && !ui.collapsed[step.ordinal]) {
                    walk(step.steps, key);
                }

                var fate = fateText(step);
                if (fate && step.fate !== "discarded" && !step.cycle) {
                    events.push({
                        kind: "return", time: ++time,
                        from: key, to: callerKey,
                        label: fate,
                        step: step
                    });
                }

                if (key !== callerKey) {
                    bars.push({ lane: key, from: callTime, to: time });
                }
            });
        }

        walk(model.steps, "t" + model.root.id);

        var laneGap = 170;
        var left = 90;
        var rowH = 24;
        var topPad = 12;
        var width = left + lanes.length * laneGap;
        var height = topPad + (time + 2) * rowH;

        var head = el("div", "flow-seq-head");
        head.style.width = width + "px";
        lanes.forEach(function (entry) {
            var label = el("span", null, entry.label);
            label.style.left = (left + entry.index * laneGap) + "px";
            head.appendChild(label);
        });
        elements.seq.appendChild(head);

        var svg = svgEl("svg", { width: width, height: height });

        lanes.forEach(function (entry) {
            var x = left + entry.index * laneGap;
            svg.appendChild(svgEl("line", {
                x1: x, y1: 4, x2: x, y2: height - 6,
                stroke: util.cssVar("--control-border"), "stroke-width": 1
            }));
        });

        bars.forEach(function (bar) {
            var x = left + laneByKey[bar.lane].index * laneGap;
            svg.appendChild(svgEl("rect", {
                x: x - 4, y: topPad + bar.from * rowH - 8,
                width: 8, height: Math.max(10, (bar.to - bar.from) * rowH + 12),
                fill: util.cssVar("--panel-raised"),
                stroke: util.cssVar("--control-border")
            }));
        });

        events.forEach(function (evt) {
            var y = topPad + evt.time * rowH;
            var x1 = left + laneByKey[evt.from].index * laneGap;
            var x2 = left + laneByKey[evt.to].index * laneGap;
            var isReturn = evt.kind === "return";
            var colour = isReturn
                ? util.cssVar("--fg-faint")
                : evt.step.cycle
                    ? util.cssVar("--warning")
                    : evt.step.unresolved || evt.step.confidence !== "unique"
                        ? util.cssVar("--edge-weak")
                        : util.cssVar("--edge");

            if (x1 === x2) {
                // A self call: the little hook every sequence notation uses.
                svg.appendChild(svgEl("path", {
                    d: "M " + x1 + " " + (y - 8) + " h 30 v 12 h -26",
                    fill: "none", stroke: colour,
                    "stroke-dasharray": isReturn || evt.step.cycle ? "4 3" : "0"
                }));
                svg.appendChild(svgEl("path", {
                    d: "M " + (x1 + 10) + " " + (y + 1) + " l 8 3 l -2 -6 z", fill: colour
                }));
            } else {
                svg.appendChild(svgEl("line", {
                    x1: x1, y1: y, x2: x2, y2: y,
                    stroke: colour, "stroke-width": 1.3,
                    "stroke-dasharray": isReturn || evt.step.cycle ? "4 3" : "0"
                }));
                var tipX = x2 > x1 ? x2 - 7 : x2 + 7;
                svg.appendChild(svgEl("path", {
                    d: "M " + x2 + " " + y + " L " + tipX + " " + (y - 4) + " L " + tipX + " " + (y + 4) + " z",
                    fill: colour
                }));
            }

            var text = svgEl("text", {
                x: Math.min(x1, x2) + Math.abs(x2 - x1) / 2,
                y: y - 5,
                "text-anchor": "middle",
                "font-size": 10,
                "font-family": util.cssVar("--font-mono"),
                fill: isReturn ? util.cssVar("--fg-muted") : util.cssVar("--fg")
            });
            text.textContent = (evt.step.cycle && !isReturn ? "↺ " : "") + evt.label;
            svg.appendChild(text);

            var hit = svgEl("rect", {
                x: Math.min(x1, x2) - 4, y: y - 16,
                width: Math.max(28, Math.abs(x2 - x1) + 8), height: 22,
                fill: "transparent"
            });
            hit.style.cursor = "pointer";
            hit.addEventListener("click", function () { selectStep(evt.step); });
            hit.addEventListener("dblclick", function () {
                if (evt.step.target) {
                    bridge.post("flowRoot", { id: evt.step.target.id });
                }
            });
            hit.addEventListener("mousemove", function (event) { showTip(evt.step, event); });
            hit.addEventListener("mouseleave", hideTip);
            svg.appendChild(hit);
        });

        elements.seq.appendChild(svg);
    }

    // ---- Flame / icicle -----------------------------------------------------

    function renderFlame() {
        util.clear(elements.flame);

        var width = Math.max(320, elements.flame.clientWidth - 20);
        var bandH = 26;

        var hierarchy = d3.hierarchy(
            { __rootBand: true, steps: model.steps },
            function (d) { return d.steps && d.steps.length ? d.steps : null; });
        hierarchy.sum(function () { return 1; });

        var height = (hierarchy.height + 1) * bandH;
        d3.partition().size([width, height])(hierarchy);

        var svg = svgEl("svg", { width: width, height: height + 4 });

        hierarchy.each(function (node) {
            var step = node.data.__rootBand ? null : node.data;
            var w = node.x1 - node.x0;
            if (w < 1) {
                return;
            }

            var fill = step
                ? (step.io
                    ? util.cssVar("--kind-io")
                    : step.unresolved
                        ? util.cssVar("--control")
                        : util.groupColour(step.target ? step.target.group : "variable"))
                : util.cssVar("--accent");

            var rect = svgEl("rect", {
                x: node.x0, y: node.y0,
                width: Math.max(1, w - 1), height: bandH - 2,
                fill: fill,
                "fill-opacity": step && (step.unresolved || step.confidence !== "unique") ? 0.35 : 0.75,
                stroke: util.cssVar("--bg"),
                "stroke-width": 1
            });
            rect.style.cursor = "pointer";
            if (step) {
                rect.addEventListener("click", function () { selectStep(step); });
                rect.addEventListener("dblclick", function () {
                    if (step.target) {
                        bridge.post("flowRoot", { id: step.target.id });
                    }
                });
                rect.addEventListener("mousemove", function (event) { showTip(step, event); });
                rect.addEventListener("mouseleave", hideTip);
            }
            svg.appendChild(rect);

            if (w > 56) {
                var label = svgEl("text", {
                    x: node.x0 + 5, y: node.y0 + bandH / 2 + 3,
                    "font-size": 10.5,
                    "font-family": util.cssVar("--font-mono"),
                    fill: util.cssVar("--fg"),
                    "pointer-events": "none"
                });
                label.textContent = util.truncate(
                    step ? step.ordinal + " " + step.name : (model.root.name || "root"),
                    Math.floor(w / 7));
                svg.appendChild(label);
            }
        });

        elements.flame.appendChild(svg);
    }

    // ---- Export -------------------------------------------------------------

    /* The flat trace document — the shape ExportedFlowDocument parses host-side, for
       both the Mermaid writer and the JSON file. */
    function buildExportDoc() {
        var steps = [];

        function walk(list) {
            list.forEach(function (step) {
                steps.push({
                    ordinal: step.ordinal,
                    name: step.name,
                    receiver: step.receiver || null,
                    args: step.args || null,
                    fate: step.fate || null,
                    fateName: step.fateName || null,
                    confidence: step.confidence,
                    candidates: step.candidates ? step.candidates.length : 0,
                    targetName: step.target ? step.target.name : null,
                    targetPath: step.target ? step.target.path : null,
                    cycle: !!step.cycle,
                    cycleOf: step.cycleOf || null,
                    unresolved: !!step.unresolved,
                    collapsedAt: step.collapsedAt || null,
                    ioDirection: step.io ? step.io.direction : null,
                    ioFamily: step.io ? step.io.family : null,
                    stepTruncated: !!(step.truncated && step.truncated.total > 0),
                    callSites: step.truncated ? step.truncated.total : 0,
                    line: step.line
                });
                if (step.steps && step.steps.length) {
                    walk(step.steps);
                }
            });
        }

        walk(model.steps);

        return {
            root: {
                id: model.root.id,
                name: model.root.name,
                kind: model.root.kind,
                path: model.root.path,
                line: model.root.line
            },
            steps: steps,
            truncated: !!model.truncated
        };
    }

    function exportView(format) {
        if (!model) {
            bridge.post("exportResult", { format: format, data: null });
            return;
        }

        if (format === "png") {
            // Only the flowchart has pixels; the honest answer elsewhere is nothing.
            var data = ui.layout === "flowchart" && cy && cy.nodes().length > 0
                ? cy.png({ full: true, scale: 2, bg: util.cssVar("--bg") })
                : null;
            bridge.post("exportResult", { format: format, data: data });
            return;
        }

        bridge.post("exportResult", {
            format: format,
            data: JSON.stringify(buildExportDoc(), null, 2)
        });
    }

    // ---- Registration ------------------------------------------------------

    buildLayoutButtons();

    window.viewHost.register("flow", {
        element: elements.section,
        onShow: function () {
            render();
        },
        onHide: function () {
            hideTip();
            hidePicker();
        },
        onResize: function () {
            if (ui.layout === "flowchart" && cy) {
                cy.resize();
            } else if (ui.layout === "sequence" || ui.layout === "flame") {
                render();
            }
        },
        onTheme: function () {
            if (cy) {
                cy.style().fromJson(flowchartStyle()).update();
            }
            if (ui.layout !== "tree") {
                render();
            }
        },
        onExport: exportView
    });
})();

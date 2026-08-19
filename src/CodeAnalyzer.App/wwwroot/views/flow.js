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
        crumbs: [],                         // previous roots [{ id, name }]
        pendingRoot: null                   // a crumb click in flight: do not re-push
    };

    /* Renderers by layout id. Flowchart, sequence and flame register here as they
       arrive; the picker is built from this table so it can never offer a dead button. */
    var RENDERERS = { tree: renderTree };
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
        renderCrumbs();

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
        onResize: function () { /* the HTML tree reflows on its own */ },
        onTheme: function () { /* colours come from CSS custom properties */ },
        onExport: function (format) {
            // PNG belongs to the flowchart layout; Mermaid/JSON to the flat document.
            // Both arrive with those renderers; until then the honest answer is nothing.
            bridge.post("exportResult", { format: format, data: null });
        }
    });
})();

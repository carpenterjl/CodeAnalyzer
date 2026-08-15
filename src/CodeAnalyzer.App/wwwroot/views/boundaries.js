/*
  I/O boundaries view.

  Two columns: data leaving the workspace on the left, data arriving on the right, both
  grouped by top-level directory — so software TX naturally sits facing firmware RX.

  Every row is a stored call site. Direction is the one deliberate step past the syntax,
  and each row says where its direction came from — the shipped catalog's documented
  contract, or the user's own mark. Gated matches state the co-occurrence rule that
  admitted them. Pairing TX with RX by protocol content is not attempted: the two columns
  put the sites side by side, and the reader reads the values.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-boundaries"),
        pane: document.getElementById("boundaries"),
        empty: document.getElementById("boundaries-empty")
    };

    var lastPayload = null;

    var EMPTY_TEXT = "No catalog API matched and nothing is marked — right-click a " +
        "function in the detail pane to mark it as an input or output boundary.";

    function setEmpty(message) {
        util.overlay(elements.empty, message);
    }

    var GLYPHS = { out: "▶", in: "◀", inout: "⇄" };

    function siteRow(site) {
        var row = el("div", "bnd-row");
        if (site.callerId) {
            row.dataset.id = site.callerId;
            row.classList.add("clickable");
        }

        var top = el("div", "bnd-row-top");
        top.appendChild(el("span", "card-name", (GLYPHS[site.direction] || "") + " " + site.name));
        top.appendChild(el("span", "badge", site.source));
        row.appendChild(top);

        // The verbatim argument list is the data on the wire; it gets the mono face.
        if (site.argText) {
            row.appendChild(el("div", "bnd-args", util.truncate(site.argText, 90)));
        }

        var where = el("div", "bnd-where");
        if (site.caller) {
            where.appendChild(el("span", null, site.caller));
            where.appendChild(el("span", "dot", "·"));
        }
        where.appendChild(el("span", null, site.path + ":" + site.line));
        row.appendChild(where);

        // Amber, same as ambiguous links: this row exists because of a name match plus a
        // co-occurring fact, and the rule is stated rather than implied.
        if (site.gateNote) {
            row.appendChild(el("div", "warn", "name match " + site.gateNote));
        }

        return row;
    }

    function column(title, glyph, groups) {
        var box = el("section", "bnd-col");

        var heading = el("h1", null, glyph + " " + title);
        box.appendChild(heading);

        if (groups.length === 0) {
            box.appendChild(el("p", "comp-none", "Nothing in this direction."));
            return box;
        }

        groups.forEach(function (group) {
            var section = el("section", "comp-section");

            var h = el("h2", null, group.label);
            var count = group.sites.length;
            h.appendChild(el("span", "count", count));
            section.appendChild(h);

            var body = el("div", "comp-section-body");
            group.sites.forEach(function (site) {
                body.appendChild(siteRow(site));
            });
            section.appendChild(body);
            box.appendChild(section);
        });

        return box;
    }

    function render(view) {
        util.clear(elements.pane);

        if (!view || view.totalSites === 0) {
            setEmpty(EMPTY_TEXT);
            return;
        }

        setEmpty(null);

        var note = el("p", "bnd-note",
            "Direction comes from the API's documented contract or your own mark, " +
            "never from the syntax. An input/output call appears in both columns.");
        elements.pane.appendChild(note);

        var columns = el("div", "bnd-columns");
        columns.appendChild(column("Outputs — data leaving", GLYPHS.out, view.outputs || []));
        columns.appendChild(column("Inputs — data arriving", GLYPHS.in, view.inputs || []));
        elements.pane.appendChild(columns);
    }

    // ---- Interaction -------------------------------------------------------

    var lastClick = { id: null, at: 0 };
    var DOUBLE_CLICK_MS = 350;

    elements.pane.addEventListener("click", function (event) {
        var target = event.target.closest("[data-id]");
        if (!target) {
            return;
        }

        var id = target.dataset.id;
        var now = Date.now();

        // Same split as the graph and the composition cards: one click shows the caller's
        // facts, two rebuild the graph around it.
        if (lastClick.id === id && now - lastClick.at < DOUBLE_CLICK_MS) {
            lastClick = { id: null, at: 0 };
            bridge.post("nodeActivated", { id: id });
            return;
        }

        lastClick = { id: id, at: now };

        elements.pane.querySelectorAll(".selected").forEach(function (node) {
            node.classList.remove("selected");
        });
        target.classList.add("selected");

        bridge.post("nodeSelected", { id: id });
    });

    // ---- Host messages -----------------------------------------------------

    bridge.on("setBoundaries", function (payload) {
        lastPayload = payload.boundaries || null;
        render(lastPayload);
    });

    window.viewHost.register("boundaries", {
        element: elements.section,
        onShow: function () {
            if (!lastPayload) {
                setEmpty(EMPTY_TEXT);
            }
        }
    });
})();

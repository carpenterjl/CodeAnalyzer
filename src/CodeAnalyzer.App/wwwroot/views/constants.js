/*
  Constants view.

  Values written in more than one place that no reference connects: a command byte spelled
  0xA5 in the C# that sends it, 165 in the C that receives it and 8'hA5 in the RTL that
  decodes it. The call graph cannot draw that link because there is no call.

  Every row is a stored literal plus what its own language's grammar says it denotes. The
  claim is narrow and the header says so: sharing a value is evidence of an agreement, not
  proof of one — two constants that both happen to be 8 are not related.

  Both filters are visible controls, never silent defaults. Hiding 0 and 1 is on by default
  because they are carried by everything, but the checkbox says it is happening.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-constants"),
        pane: document.getElementById("constants"),
        empty: document.getElementById("constants-empty")
    };

    var lastPayload = null;

    // Mirrors the host's ConstantsOptions default. The page owns the controls; the host
    // owns the query, and this is the state the two agree on.
    var options = { acrossDirectories: false, includeTrivial: false };

    var EMPTY_TEXT = "No value is defined in two languages. Switch the filter to compare " +
        "top-level directories instead, or include 0 and 1.";

    var EMPTY_DIRS_TEXT = "No value is defined in two top-level directories.";

    function setEmpty(message) {
        util.overlay(elements.empty, message);
    }

    function requestReload() {
        bridge.post("constantsOptions", {
            acrossDirectories: options.acrossDirectories,
            includeTrivial: options.includeTrivial
        });
    }

    // ---- Controls ----------------------------------------------------------

    function toggle(label, checked, onChange) {
        var wrap = el("label", "const-toggle");

        var box = document.createElement("input");
        box.type = "checkbox";
        box.checked = checked;
        box.addEventListener("change", function () {
            onChange(box.checked);
        });

        wrap.appendChild(box);
        wrap.appendChild(el("span", null, label));
        return wrap;
    }

    function controls() {
        var bar = el("div", "const-controls");

        bar.appendChild(toggle(
            "Compare top-level directories instead of languages",
            options.acrossDirectories,
            function (on) {
                options.acrossDirectories = on;
                requestReload();
            }));

        bar.appendChild(toggle(
            "Include 0 and 1",
            options.includeTrivial,
            function (on) {
                options.includeTrivial = on;
                requestReload();
            }));

        return bar;
    }

    // ---- Rendering ---------------------------------------------------------

    function memberRow(member) {
        var row = el("div", "const-row clickable");
        row.dataset.id = member.id;

        var top = el("div", "const-row-top");
        top.appendChild(el("span", "card-name", member.name));
        top.appendChild(el("span", "badge", member.language));
        row.appendChild(top);

        // The verbatim literal is the fact this row exists to show, in that language's own
        // notation, so it gets the mono face.
        if (member.verbatim) {
            row.appendChild(el("div", "const-verbatim", util.truncate(member.verbatim, 60)));
        }

        var where = el("div", "const-where");
        where.appendChild(el("span", null, member.kind));
        where.appendChild(el("span", "dot", "·"));
        where.appendChild(el("span", null, member.path + ":" + member.line));
        row.appendChild(where);

        return row;
    }

    function groupSection(group, acrossDirectories) {
        var section = el("section", "comp-section");

        var heading = el("h2", null, group.value);
        heading.appendChild(el("span", "count", group.total));
        section.appendChild(heading);

        // Which crossing this group actually is. Stated per group rather than only in the
        // header, because it is the reason the group is on screen at all.
        var spans = acrossDirectories ? group.directories : group.languages;
        section.appendChild(el("div", "const-spans", (spans || []).join(" · ")));

        var body = el("div", "comp-section-body");
        (group.members || []).forEach(function (member) {
            body.appendChild(memberRow(member));
        });

        // The group counts every definition; the list shows a page of them. Saying so beats
        // a list that quietly stops.
        var shown = (group.members || []).length;
        if (group.total > shown) {
            body.appendChild(el("div", "const-more",
                (group.total - shown) + " more not listed"));
        }

        section.appendChild(body);
        return section;
    }

    function render(view) {
        util.clear(elements.pane);

        if (!view) {
            setEmpty(EMPTY_TEXT);
            return;
        }

        options.acrossDirectories = view.acrossDirectories;
        options.includeTrivial = view.includeTrivial;

        elements.pane.appendChild(controls());

        if (!view.groups || view.groups.length === 0) {
            setEmpty(view.acrossDirectories ? EMPTY_DIRS_TEXT : EMPTY_TEXT);
            return;
        }

        setEmpty(null);

        var note = el("p", "bnd-note",
            "Showing " + view.criterion + ". Two definitions share a value because their " +
            "literals denote the same number or the same characters — evidence of an " +
            "agreement, not proof of one.");
        elements.pane.appendChild(note);

        var list = el("div", "const-groups");
        view.groups.forEach(function (group) {
            list.appendChild(groupSection(group, view.acrossDirectories));
        });
        elements.pane.appendChild(list);
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

        // The same split as every other list in the app: one click shows the definition's
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

    bridge.on("setConstants", function (payload) {
        lastPayload = payload.constants || null;
        render(lastPayload);
    });

    window.viewHost.register("constants", {
        element: elements.section,
        onShow: function () {
            if (!lastPayload) {
                setEmpty(EMPTY_TEXT);
            }
        }
    });
})();

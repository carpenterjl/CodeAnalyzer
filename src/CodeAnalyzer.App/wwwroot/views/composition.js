/*
  Composition inspector.

  What a struct, class or module is made of. This is the one view with no graph in it, on
  purpose: a struct's fields and a Verilog module's ports are not links anybody follows,
  they are the shape of the thing, and a ring of nodes says that far worse than a list of
  cards does.

  Every card shows only stored facts — declared type, literal initialiser, source line, and
  counts read from the index. Where the resolver was unsure, the card says so rather than
  presenting a name match as a certainty.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-composition"),
        pane: document.getElementById("composition"),
        empty: document.getElementById("composition-empty")
    };

    var lastPayload = null;

    function setEmpty(message) {
        util.overlay(elements.empty, message);
    }

    // ---- Cards -------------------------------------------------------------

    function badge(text) {
        return el("span", "badge", text);
    }

    function header(view) {
        var box = el("header", "comp-header");

        var title = el("div", "comp-title");
        title.appendChild(el("span", "comp-name", view.name));
        title.appendChild(badge(view.kind));
        if (view.modifiers) {
            title.appendChild(badge(view.modifiers));
        }
        box.appendChild(title);

        var where = el("div", "comp-sub");
        where.appendChild(el("span", null, view.path + ":" + view.line));
        where.appendChild(el("span", "dot", "·"));
        where.appendChild(el("span", null, view.language));
        if (view.containerName) {
            where.appendChild(el("span", "dot", "·"));
            where.appendChild(el("span", null, "in " + view.containerName));
        }
        box.appendChild(where);

        if (view.signature) {
            box.appendChild(el("div", "comp-signature", view.signature));
        }

        if (view.type) {
            box.appendChild(el("div", "comp-sub", "type: " + view.type));
        }

        if (view.value) {
            var value = el("div", "comp-value");
            value.appendChild(el("span", "comp-value-label", "value"));
            value.appendChild(el("span", "mono", view.value));
            box.appendChild(value);
        }

        return box;
    }

    function memberCard(member) {
        var card = el("article", "card");
        card.dataset.id = member.id;
        card.style.borderLeftColor = util.groupColour(member.group);

        var top = el("div", "card-top");
        top.appendChild(el("span", "card-name", member.name));
        top.appendChild(badge(member.kind));
        card.appendChild(top);

        // The verbatim modifier list, as its own pill: "private static readonly" is a
        // fact the card can state without the reader opening the file.
        if (member.modifiers) {
            var pills = el("div", "card-facts");
            pills.appendChild(badge(member.modifiers));
            card.appendChild(pills);
        }

        if (member.type) {
            card.appendChild(el("div", "card-type", member.type));
        }

        if (member.value) {
            card.appendChild(el("div", "card-value", "= " + util.truncate(member.value, 80)));
        }

        var facts = el("div", "card-facts");
        facts.appendChild(el("span", null, "line " + member.line));

        // Only stated when there is something to state. "0 references" would read as a
        // claim that nothing uses it, and an unresolved reference is not a non-existent one.
        if (member.references > 0) {
            facts.appendChild(el("span", "dot", "·"));
            facts.appendChild(el("span", null, util.count(member.references, "reference", "references")));
        }

        if (member.members > 0) {
            facts.appendChild(el("span", "dot", "·"));
            facts.appendChild(el("span", null, util.count(member.members, "member", "members")));
        }

        card.appendChild(facts);
        return card;
    }

    function linkRow(link) {
        var row = el("div", "link-row");
        if (link.id) {
            row.dataset.id = link.id;
            row.classList.add("clickable");
        }

        row.appendChild(el("span", "card-name", link.name));

        if (link.kind) {
            row.appendChild(badge(link.kind));
        }

        var where = el("span", "link-where");
        if (link.path) {
            where.textContent = link.path;
        } else {
            // No target: the name was written here but nothing in the workspace defines it.
            where.textContent = "not defined in this workspace";
            where.classList.add("faint");
        }
        row.appendChild(where);

        if (link.confidence && link.confidence !== "unique") {
            row.appendChild(el("span", "warn", link.confidenceLabel));
        }

        return row;
    }

    function section(title, nodes) {
        if (nodes.length === 0) {
            return null;
        }

        var box = el("section", "comp-section");
        box.appendChild(el("h2", null, title));

        var body = el("div", "comp-section-body");
        nodes.forEach(function (node) {
            body.appendChild(node);
        });
        box.appendChild(body);
        return box;
    }

    /* Members keep source order, but grouping by kind is what turns a Verilog module's
       forty declarations into "parameters, ports, then everything else". */
    function memberSections(members) {
        var groups = [];
        var byKind = Object.create(null);

        members.forEach(function (member) {
            if (!byKind[member.kind]) {
                byKind[member.kind] = [];
                groups.push(member.kind);
            }
            byKind[member.kind].push(member);
        });

        return groups.map(function (kind) {
            var cards = byKind[kind].map(memberCard);
            var box = el("section", "comp-section");

            var heading = el("h2", null, kind + (cards.length === 1 ? "" : "s"));
            heading.appendChild(el("span", "count", cards.length));
            box.appendChild(heading);

            var grid = el("div", "card-grid");
            cards.forEach(function (card) {
                grid.appendChild(card);
            });
            box.appendChild(grid);
            return box;
        });
    }

    function render(view) {
        util.clear(elements.pane);

        if (!view) {
            setEmpty("Select a symbol to see what it is made of.");
            return;
        }

        setEmpty(null);
        elements.pane.appendChild(header(view));

        var sections = memberSections(view.members || []);

        // The base list is one flat fact in the syntax; which entries are interfaces is
        // known only from what each name resolved to, so the split keys on the target's
        // kind. Unresolved names stay under the neutral heading — claiming either way
        // would be a guess.
        var baseTypes = view.baseTypes || [];
        var implementsLinks = baseTypes.filter(function (link) {
            return link.kind === "interface";
        });
        var extendsLinks = baseTypes.filter(function (link) {
            return link.kind && link.kind !== "interface";
        });
        var unresolvedBases = baseTypes.filter(function (link) {
            return !link.kind;
        });

        [
            ["Instantiates", view.instantiates],
            ["Instantiated by", view.instantiatedBy],
            ["Extends", extendsLinks],
            ["Implements", implementsLinks],
            ["Inherits from (unresolved)", unresolvedBases],
            ["Inherited by", view.derivedTypes]
        ].forEach(function (entry) {
            var built = section(entry[0], (entry[1] || []).map(linkRow));
            if (built) {
                sections.push(built);
            }
        });

        if (sections.length === 0) {
            var note = el("p", "comp-none",
                "This symbol has no members, instantiations or type relations in the index.");
            elements.pane.appendChild(note);
            return;
        }

        sections.forEach(function (built) {
            elements.pane.appendChild(built);
        });
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

        // Same split as the graph: one click shows the facts, two make it the new subject.
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

    bridge.on("setComposition", function (payload) {
        lastPayload = payload.composition || null;
        render(lastPayload);
    });

    window.viewHost.register("composition", {
        element: elements.section,
        onShow: function () {
            if (!lastPayload) {
                setEmpty("Select a symbol to see what it is made of.");
            }
        },
        onTheme: function () {
            // Card accents are read from CSS variables at build time, so redraw them.
            if (lastPayload) {
                render(lastPayload);
            }
        }
    });
})();

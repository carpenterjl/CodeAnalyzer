/*
  Neighbourhood renderer.

  Draws exactly what the host sends and nothing more: node labels, constant values and
  edge styling all come straight from the indexed facts. Where the resolver was unsure,
  the edge is drawn dashed or dotted rather than being quietly promoted to a solid line.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var cssVar = window.viewUtil.cssVar;

    cytoscape.use(window.cytoscapeFcose);
    cytoscape.use(window.cytoscapeDagre);

    var GROUPS = ["function", "type", "constant", "macro", "module", "variable"];

    /* Beyond this, animating a relayout costs more than it communicates. */
    var ANIMATION_NODE_LIMIT = 120;

    /* Two taps on the same node within this many ms re-root the graph there. */
    var DOUBLE_TAP_MS = 350;

    var elements = {
        canvas: document.getElementById("canvas"),
        overlay: document.getElementById("overlay"),
        overlayText: document.getElementById("overlay-text"),
        truncation: document.getElementById("truncation"),
        popover: document.getElementById("popover"),
        popoverTitle: document.getElementById("popover-title"),
        popoverSub: document.getElementById("popover-sub"),
        expandCallers: document.getElementById("expand-callers"),
        expandCallees: document.getElementById("expand-callees"),
        popoverSites: document.getElementById("popover-sites"),
        legend: document.getElementById("legend"),
        legendSmaller: document.getElementById("legend-smaller"),
        legendLarger: document.getElementById("legend-larger"),
        legendDetails: document.getElementById("legend-details"),
        hiddenBar: document.getElementById("hidden-bar"),
        hiddenCount: document.getElementById("hidden-count"),
        showHidden: document.getElementById("show-hidden"),
        contextMenu: document.getElementById("context-menu"),
        contextMenuTitle: document.getElementById("context-menu-title"),
        contextMenuItems: document.getElementById("context-menu-items")
    };

    var currentLayout = "force";
    var lastTap = { id: null, at: 0 };

    // Default mirrors SessionState.GraphNodeDetails in Core. Declared here, above
    // toElements, because the first setGraph can arrive before the host says anything.
    var showNodeDetails = true;

    var cy = cytoscape({
        container: elements.canvas,
        style: buildStyle(),
        minZoom: 0.1,
        maxZoom: 3
    });

    // ---- Styling -----------------------------------------------------------

    function buildStyle() {
        var accent = cssVar("--accent");

        var style = [
            {
                /*
                  The node is a picture of itself: the sectioned tile (header band, name,
                  signature/value, origin) is baked into an SVG background image by
                  buildTile, so the canvas draws no label of its own. Baking keeps zoom,
                  pan and PNG export on the one layer cytoscape already renders — no DOM
                  overlay to keep in sync.
                */
                selector: "node",
                style: {
                    label: "",
                    shape: "round-rectangle",
                    "corner-radius": 6,
                    width: "data(tileW)",
                    height: "data(tileH)",
                    "background-image": "data(tile)",
                    "background-width": "100%",
                    "background-height": "100%",
                    "background-opacity": 0,
                    "border-width": 0,
                    "transition-property": "outline-width, outline-color",
                    "transition-duration": 120
                }
            },
            {
                /* The symbol the fragment was built around: the design system's cyan
                   focus ring, offset outside the tile's own kind-coloured border. */
                selector: "node[?isFocus]",
                style: {
                    "outline-width": 3,
                    "outline-color": accent,
                    "outline-offset": 3
                }
            },
            {
                selector: "node:selected",
                style: {
                    "outline-width": 2,
                    "outline-color": accent,
                    "outline-offset": 3
                }
            },
            {
                /*
                  Every edge is a bowed bezier rather than a straight line: with the big
                  rectangular tiles a straight run reads as a collision, and a curve can
                  slide past a tile it would otherwise cross. The bow distance is per-edge
                  data assigned by assignEdgeBows, which fans parallel links apart instead
                  of stacking them.
                */
                selector: "edge",
                style: {
                    width: 1.6,
                    "curve-style": "unbundled-bezier",
                    "control-point-distances": "data(bow)",
                    "control-point-weights": 0.5,
                    "target-arrow-shape": "triangle",
                    "arrow-scale": 1,
                    "line-color": cssVar("--edge"),
                    "target-arrow-color": cssVar("--edge"),
                    opacity: 0.85
                }
            },
            {
                /* Recursion: a loop has no pair to bow against, so it keeps the classic
                   loop rendering instead of a data-driven distance. */
                selector: "edge:loop",
                style: {
                    "curve-style": "bezier",
                    "control-point-step-size": 60
                }
            },
            {
                /* Several definitions share the name; the resolver picked none of them. */
                selector: 'edge[confidence = "ambiguous"]',
                style: { "line-style": "dashed", "line-dash-pattern": [6, 3] }
            },
            {
                /* Matched only by name, across a language boundary. */
                selector: 'edge[confidence = "weak"]',
                style: {
                    "line-style": "dotted",
                    "line-color": cssVar("--edge-weak"),
                    "target-arrow-color": cssVar("--edge-weak")
                }
            },
            {
                selector: "edge:selected",
                style: {
                    width: 2.6,
                    "line-color": cssVar("--accent"),
                    "target-arrow-color": cssVar("--accent")
                }
            },
            {
                /* Everything not connected to the selection fades back. */
                selector: ".dimmed",
                style: { opacity: 0.2 }
            },
            {
                /*
                  Taken out of the picture by the right-click menu. display:none rather
                  than a removal, so the element keeps counting toward the expand badges:
                  hiding is a change to the view, not to what the index found, and an
                  expansion that re-fetched an already-loaded neighbour would add nothing.
                */
                selector: ".user-hidden",
                style: { display: "none" }
            }
        ];

        // Kind colour, visibility border style (dashed internal, dotted private) and
        // the override italic are all baked into the tile image by buildTile — the
        // stylesheet no longer encodes them, because the tile is the encoding.

        // Inherits edges carry the UML-familiar hollow triangle. line-style stays free
        // for the confidence encoding, so the two never collide.
        style.push({
            selector: 'edge[kind = "inherits"]',
            style: {
                width: 2,
                "target-arrow-shape": "triangle",
                "target-arrow-fill": "hollow",
                "arrow-scale": 1.15
            }
        });

        // I/O boundary stub links. The stub node itself is an ordinary tile (buildTile
        // gives it the io-coloured band), so only its edge needs styling here: it draws
        // where data leaves or enters the workspace, and its direction came from the
        // catalog's documented contract or the user's own mark, never from the syntax.
        var ioColour = cssVar("--kind-io");
        style.push({
            selector: 'edge[kind = "io"]',
            style: {
                width: 1.4,
                "line-color": ioColour,
                "target-arrow-color": ioColour,
                "source-arrow-color": ioColour,
                "target-arrow-shape": "triangle",
                "arrow-scale": 0.75
            }
        });
        style.push({
            // An inout API sends and receives in one call, so its link points both ways.
            selector: "edge[?bidirectional]",
            style: { "source-arrow-shape": "triangle" }
        });
        style.push({
            // Stubs are all detail, so the DETAIL toggle takes them with it.
            selector: ".detail-hidden",
            style: { display: "none" }
        });

        return style;
    }

    // Mirrors ModifierFacts in Core: longest token first, whole-token match only.
    var VISIBILITY_TOKENS = [
        "protected internal", "private protected",
        "public", "internal", "protected", "private"
    ];

    function hasToken(modifiers, token) {
        if (!modifiers) {
            return false;
        }
        return (" " + modifiers + " ").indexOf(" " + token + " ") >= 0;
    }

    function visibilityToken(modifiers) {
        for (var i = 0; i < VISIBILITY_TOKENS.length; i++) {
            if (hasToken(modifiers, VISIBILITY_TOKENS[i])) {
                return VISIBILITY_TOKENS[i];
            }
        }
        return "";
    }

    function applyTheme() {
        // views.js owns the data-theme attribute; this only has to restate the palette,
        // because cytoscape resolves colours once when the stylesheet is built — and the
        // tiles are pictures, so they are rebaked from the new palette as well.
        cy.style().fromJson(buildStyle()).update();
        retileAll();
    }

    // ---- Tile rendering ----------------------------------------------------

    /*
      Every node is a sectioned tile, per the design system's graph-tile spec: kind rides
      in a coloured header band, and the body is split into labelled sections — name,
      signature (or value, or the wire text for a stub), origin. The parameter list is
      reduced to a count in the band, so a name is never broken by an ellipsis: names
      wrap instead, and the tile grows to fit. The popover still carries every untruncated
      fact for anyone who wants the long form.

      The tile is drawn as an SVG data URI and set as the node's background image. The
      SVG's intrinsic size is 2× the node box, so the rasterised texture stays crisp when
      the user zooms in.
    */
    var DIRECTION_GLYPHS = { out: "▶", in: "◀", inout: "⇄" };

    var measureText = document.createElement("canvas").getContext("2d");

    var TILE = {
        padX: 12,       /* body text inset */
        bandH: 22,      /* kind header band */
        bandPadX: 10,
        nameLineH: 18,  /* 13px mono name lines */
        namePadTop: 7,
        namePadBottom: 8,
        rowH: 24,       /* signature / value / wire rows */
        originH: 22,    /* darker footer with file:line */
        minInner: 132,
        maxInner: 264,  /* names longer than this wrap; rows get an ellipsis */
        maxNameLines: 3,
        radius: 6
    };

    function xmlEscape(text) {
        return String(text)
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;");
    }

    /*
      The band's label-caps run at 9.5px/700 with .09em tracking; measureText knows
      nothing about tracking, so it is added back per glyph.
    */
    var CAPS_TRACKING = 0.855;

    function capsWidth(text, font) {
        measureText.font = font;
        return measureText.measureText(text).width + text.length * CAPS_TRACKING;
    }

    /* The body faces are monospace, so one glyph measured once prices every string. */
    function monoCharWidth(size, family) {
        measureText.font = size + "px " + family;
        return measureText.measureText("MMMMMMMMMM").width / 10;
    }

    function clip(text, maxChars) {
        return text.length > maxChars ? text.slice(0, Math.max(1, maxChars - 1)) + "…" : text;
    }

    /* file paths read from the right, so a long origin keeps its tail, not its head. */
    function clipLeading(text, maxChars) {
        return text.length > maxChars ? "…" + text.slice(text.length - maxChars + 1) : text;
    }

    /* Top-level comma count; nested generics, arrays and function pointers don't split. */
    function paramCount(params) {
        var inner = params.replace(/^\s*\(/, "").replace(/\)\s*$/, "").trim();
        if (!inner || inner === "void") {
            return 0;
        }

        var depth = 0;
        var count = 1;
        for (var i = 0; i < inner.length; i++) {
            var ch = inner[i];
            if (ch === "(" || ch === "[" || ch === "<" || ch === "{") {
                depth++;
            } else if (ch === ")" || ch === "]" || ch === ">" || ch === "}") {
                depth--;
            } else if (ch === "," && depth === 0) {
                count++;
            }
        }
        return count;
    }

    /*
      Resolved from the live CSS custom properties once per rebuild pass, never hard-coded,
      so the tiles follow a theme switch exactly the way the rest of the page does.
    */
    function tileColours() {
        var kinds = {};
        GROUPS.forEach(function (group) {
            kinds[group] = cssVar("--kind-" + group);
        });
        kinds.interface = cssVar("--kind-interface");
        kinds.io = cssVar("--kind-io");

        return {
            kinds: kinds,
            bg: cssVar("--panel"),
            /* The canvas colour doubles as the band's text: dark navy on the pastel
               bands in dark, near-white on the saturated bands in light. */
            bandFg: cssVar("--bg"),
            divider: cssVar("--control-high"),
            originBg: cssVar("--bg"),
            fg: cssVar("--fg"),
            fgMuted: cssVar("--fg-muted"),
            fgFaint: cssVar("--fg-faint"),
            valueFg: cssVar("--kind-constant"),
            /* SVG attributes are double-quoted, so the stacks trade their quotes in. */
            mono: cssVar("--font-mono").replace(/"/g, "'"),
            ui: cssVar("--font-ui").replace(/"/g, "'")
        };
    }

    function capsText(x, y, fill, text, opts) {
        return '<text x="' + x + '" y="' + y + '"' +
            (opts && opts.anchor ? ' text-anchor="' + opts.anchor + '"' : "") +
            ' font-family="' + (opts && opts.family) + '" font-size="9.5" font-weight="700"' +
            ' letter-spacing="0.85" fill="' + fill + '"' +
            (opts && opts.opacity ? ' fill-opacity="' + opts.opacity + '"' : "") +
            ">" + xmlEscape(text) + "</text>";
    }

    function monoText(x, y, size, fill, text, family, italic) {
        return '<text x="' + x + '" y="' + y + '" font-family="' + family + '"' +
            ' font-size="' + size + '"' + (italic ? ' font-style="italic"' : "") +
            ' fill="' + fill + '">' + xmlEscape(text) + "</text>";
    }

    function buildTile(data, c) {
        var kindColour = data.isIoStub ? c.kinds.io
            : data.kind === "interface" ? c.kinds.interface
                : (c.kinds[data.group] || c.fgFaint);

        // The container prefix is what tells two same-named members apart without a
        // click — Device.Send vs Radio.Send. Both halves are indexed facts.
        var name = data.isIoStub
            ? data.name
            : (data.container ? data.container + "." + data.name : data.name);

        var bandLeft;
        var bandRight;
        if (data.isIoStub) {
            bandLeft = (DIRECTION_GLYPHS[data.direction] || "") + " " +
                data.directionLabel + " · i/o boundary";
            bandRight = data.source || "";
        } else {
            bandLeft = (data.visibility ? data.visibility + " " : "") + data.kind;
            var params = showNodeDetails && data.params ? paramCount(data.params) : null;
            bandRight = data.isFocus ? "focus"
                : params !== null ? params + (params === 1 ? " param" : " params")
                    : "";
        }
        bandLeft = bandLeft.toUpperCase();
        bandRight = bandRight.toUpperCase();

        // Body rows, in reading order. A constant's value is one of the facts worth
        // seeing without clicking, so it stays even with details off; the signature and
        // the stub's wire text are exactly what the DETAIL toggle exists to fold away.
        var rows = [];
        if (showNodeDetails && !data.isIoStub && data.params) {
            rows.push({ cap: "SIGNATURE", text: data.params, fill: c.fgMuted });
        }
        if (showNodeDetails && data.isIoStub && data.argText) {
            rows.push({ cap: "ON THE WIRE", text: data.argText, fill: c.fgMuted });
        }
        if (data.value) {
            rows.push({ cap: "VALUE", text: data.value, fill: c.valueFg });
        }

        var origin = showNodeDetails && !data.isIoStub && data.path
            ? data.path + ":" + data.line
            : "";

        // ---- Sizing. The name dictates the width (it never truncates); everything
        // else asks for room but settles for an ellipsis at maxInner.
        var capsFont = "700 9.5px " + c.ui;
        var w13 = monoCharWidth(13, c.mono);
        var w11 = monoCharWidth(11, c.mono);
        var w10 = monoCharWidth(10, c.mono);

        var need = TILE.minInner;
        need = Math.max(need, capsWidth(bandLeft, capsFont) +
            (bandRight ? capsWidth(bandRight, capsFont) + 14 : 0));
        need = Math.max(need, Math.min(name.length * w13, TILE.maxInner));
        rows.forEach(function (row) {
            need = Math.max(need, Math.min(
                capsWidth(row.cap, capsFont) + 8 + row.text.length * w11, TILE.maxInner));
        });
        if (origin) {
            need = Math.max(need, Math.min(origin.length * w10, TILE.maxInner));
        }

        var inner = Math.ceil(Math.min(need, TILE.maxInner));
        var w = inner + TILE.padX * 2;

        // Identifiers carry no spaces, so the wrap is a plain character fill — the same
        // break-anywhere the design's HTML tiles use.
        var perLine = Math.max(4, Math.floor(inner / w13));
        var nameLines = [];
        for (var i = 0; i < name.length && nameLines.length < TILE.maxNameLines; i += perLine) {
            nameLines.push(name.slice(i, i + perLine));
        }
        if (name.length > perLine * TILE.maxNameLines) {
            nameLines[TILE.maxNameLines - 1] =
                clip(nameLines[TILE.maxNameLines - 1], perLine);
        }

        var nameBlockH = TILE.namePadTop + nameLines.length * TILE.nameLineH + TILE.namePadBottom;
        var h = TILE.bandH + nameBlockH + rows.length * TILE.rowH + (origin ? TILE.originH : 0);

        // ---- Drawing.
        var r = TILE.radius;
        var parts = [];
        parts.push('<svg xmlns="http://www.w3.org/2000/svg" width="' + w * 2 +
            '" height="' + h * 2 + '" viewBox="0 0 ' + w + " " + h + '">');

        parts.push('<rect x="0.5" y="0.5" width="' + (w - 1) + '" height="' + (h - 1) +
            '" rx="' + r + '" fill="' + c.bg + '"/>');

        // Header band, rounded along the top only.
        parts.push('<path d="M0.5 ' + TILE.bandH + " V" + (r + 0.5) +
            " a" + r + " " + r + " 0 0 1 " + r + " -" + r +
            " H" + (w - r - 0.5) +
            " a" + r + " " + r + " 0 0 1 " + r + " " + r +
            " V" + TILE.bandH + ' Z" fill="' + kindColour + '"/>');
        parts.push(capsText(TILE.bandPadX, 14.5, c.bandFg, bandLeft, { family: c.ui }));
        if (bandRight) {
            parts.push(capsText(w - TILE.bandPadX, 14.5, c.bandFg, bandRight,
                { family: c.ui, anchor: "end", opacity: 0.72 }));
        }

        // The name never truncates; the override italic is a token match on the stored
        // modifier list, not a guess about dispatch.
        var y = TILE.bandH + TILE.namePadTop + 13;
        nameLines.forEach(function (line) {
            parts.push(monoText(TILE.padX, y, 13, c.fg, line, c.mono, data.isOverride));
            y += TILE.nameLineH;
        });

        var top = TILE.bandH + nameBlockH;
        rows.forEach(function (row) {
            parts.push('<line x1="1" y1="' + (top + 0.5) + '" x2="' + (w - 1) +
                '" y2="' + (top + 0.5) + '" stroke="' + c.divider + '"/>');
            var capW = Math.ceil(capsWidth(row.cap, capsFont));
            parts.push(capsText(TILE.padX, top + 15.5, c.fgFaint, row.cap, { family: c.ui }));
            var maxChars = Math.floor((inner - capW - 8) / w11);
            parts.push(monoText(TILE.padX + capW + 8, top + 15.5, 11, row.fill,
                clip(row.text, maxChars), c.mono));
            top += TILE.rowH;
        });

        if (origin) {
            // Footer on the lowest surface, rounded along the bottom only.
            parts.push('<path d="M0.5 ' + top + " H" + (w - 0.5) +
                " V" + (h - r - 0.5) +
                " a" + r + " " + r + " 0 0 1 -" + r + " " + r +
                " H" + (r + 0.5) +
                " a" + r + " " + r + " 0 0 1 -" + r + " -" + r +
                " V" + top + ' Z" fill="' + c.originBg + '"/>');
            parts.push('<line x1="1" y1="' + (top + 0.5) + '" x2="' + (w - 1) +
                '" y2="' + (top + 0.5) + '" stroke="' + c.divider + '"/>');
            parts.push(monoText(TILE.padX, top + 14.5, 10, c.fgFaint,
                clipLeading(origin, Math.floor(inner / w10)), c.mono));
        }

        // Border last, over the band and footer fills, so the edge stays clean.
        // Visibility rides on its style, as chosen by the user: solid for public (and
        // for declarations that state nothing), dashed for internal, dotted for
        // private/protected.
        var dash = data.visibility === "internal" || data.visibility === "protected internal"
            ? ' stroke-dasharray="5 3"'
            : data.visibility && data.visibility.indexOf("p") === 0 && data.visibility !== "public"
                ? ' stroke-dasharray="2 3"'
                : "";
        parts.push('<rect x="0.5" y="0.5" width="' + (w - 1) + '" height="' + (h - 1) +
            '" rx="' + r + '" fill="none" stroke="' + kindColour + '" stroke-width="1"' +
            dash + "/>");

        parts.push("</svg>");

        return {
            uri: "data:image/svg+xml;utf8," + encodeURIComponent(parts.join("")),
            w: w,
            h: h
        };
    }

    function bakeTile(data, colours) {
        var tile = buildTile(data, colours);
        data.tile = tile.uri;
        data.tileW = tile.w;
        data.tileH = tile.h;
    }

    /*
      Tiles are baked into element data at construction, so changing what a tile says —
      the DETAIL toggle, a theme switch — means rebaking every one of them. Everything
      buildTile reads is already on the node, so this needs no round trip to the host.
    */
    function retileAll() {
        var colours = tileColours();
        cy.batch(function () {
            cy.nodes().forEach(function (node) {
                var data = node.data();
                var tile = buildTile(data, colours);
                node.data("tile", tile.uri);
                node.data("tileW", tile.w);
                node.data("tileH", tile.h);
            });
        });
    }

    // ---- Element mapping ---------------------------------------------------

    function toElements(graph) {
        var result = [];
        var colours = tileColours();

        (graph.nodes || []).forEach(function (node) {
            var data = {
                    id: node.id,
                    name: node.name,
                    kind: node.kind,
                    group: node.group,
                    path: node.path,
                    line: node.line,
                    value: node.value || "",
                    signature: node.signature || "",
                    params: node.params || "",
                    descriptor: node.descriptor || "",
                    modifiers: node.modifiers || "",
                    container: node.container || "",
                    visibility: visibilityToken(node.modifiers),
                    isOverride: hasToken(node.modifiers, "override"),
                    isFocus: Boolean(node.isFocus),
                    totalCallers: node.totalCallers || 0,
                    totalCallees: node.totalCallees || 0,
                    hiddenCallers: 0,
                    hiddenCallees: 0
                };
            bakeTile(data, colours);
            result.push({ group: "nodes", data: data });
        });

        (graph.edges || []).forEach(function (edge) {
            result.push({
                group: "edges",
                data: {
                    id: edge.id,
                    source: edge.source,
                    target: edge.target,
                    kind: edge.kind,
                    kindId: edge.kindId,
                    confidence: edge.confidence,
                    confidenceLabel: edge.confidenceLabel,
                    line: edge.line,
                    candidates: edge.candidates,
                    callSites: edge.callSites || 1,
                    // Refined by assignEdgeBows once every edge of the picture is known.
                    bow: 20
                }
            });
        });

        (graph.ioStubs || []).forEach(function (stub) {
            var data = {
                    id: stub.id,
                    name: stub.name,
                    isIoStub: true,
                    direction: stub.direction,
                    directionLabel: stub.directionLabel,
                    source: stub.source,
                    gateNote: stub.gateNote || "",
                    argText: stub.argText || "",
                    siteCount: stub.siteCount || 1,
                    refIds: stub.refIds || [],
                    // The fields every node handler reads, so a stub never breaks them.
                    container: "",
                    kind: "io",
                    group: "io",
                    path: "",
                    line: 0,
                    totalCallers: 0,
                    totalCallees: 0,
                    hiddenCallers: 0,
                    hiddenCallees: 0
                };
            bakeTile(data, colours);
            result.push({ group: "nodes", data: data });

            // Output points at the stub, input comes from it; an inout link carries an
            // arrow at both ends via the bidirectional flag.
            var outward = stub.direction !== "in";
            result.push({
                group: "edges",
                data: {
                    id: stub.id + ":link",
                    source: outward ? stub.caller : stub.id,
                    target: outward ? stub.id : stub.caller,
                    kind: "io",
                    kindId: -1,
                    confidence: "unique",
                    confidenceLabel: "boundary",
                    line: 0,
                    candidates: 1,
                    callSites: stub.siteCount || 1,
                    bidirectional: stub.direction === "inout",
                    bow: 20
                }
            });
        });

        return result;
    }

    /*
      Bow distances for the bezier edges. A lone edge gets a gentle constant bow — the
      curve is what lets it slide past tiles a straight line would cross — and parallel
      edges between the same pair fan out evenly instead of stacking. The sign is fixed
      against the pair's canonical order, so two opposite-direction links between the
      same two tiles bow apart rather than onto each other.
    */
    function assignEdgeBows() {
        var byPair = {};
        cy.edges().forEach(function (edge) {
            var s = edge.data("source");
            var t = edge.data("target");
            var key = s < t ? s + "\u0000" + t : t + "\u0000" + s;
            (byPair[key] = byPair[key] || []).push(edge);
        });

        Object.keys(byPair).forEach(function (key) {
            var list = byPair[key];
            var first = key.split("\u0000")[0];
            list.forEach(function (edge, index) {
                var bow = list.length === 1 ? 20 : (index - (list.length - 1) / 2) * 44;
                if (edge.data("source") !== first) {
                    bow = -bow;
                }
                edge.data("bow", bow);
            });
        });
    }

    /*
      The DETAIL toggle takes the stubs with it: they are all detail, and a crowded canvas
      scanned with details off should read as the plain call graph.
    */
    function applyStubVisibility() {
        var stubs = cy.nodes("[?isIoStub]");
        if (stubs.length === 0) {
            return;
        }

        var affected = stubs.union(stubs.connectedEdges());
        if (showNodeDetails) {
            affected.removeClass("detail-hidden");
        } else {
            affected.addClass("detail-hidden");
        }
    }

    // ---- Layout ------------------------------------------------------------

    function layoutOptions(name, seeded) {
        var animate = cy.nodes().length <= ANIMATION_NODE_LIMIT;

        if (name === "hierarchy") {
            return {
                name: "dagre",
                rankDir: "LR",
                // Spacing is scaled to the sectioned tiles: a tile is roughly three
                // times the old shape's footprint, and the bowed edges need lane room.
                nodeSep: 44,
                rankSep: 150,
                edgeSep: 20,
                padding: 40,
                animate: animate,
                animationDuration: 260,
                nodeDimensionsIncludeLabels: true
            };
        }

        return {
            name: "fcose",
            quality: "default",
            // Seeded runs keep the nodes the user has already placed mentally where they
            // are, so expanding feels like growth rather than a reshuffle.
            randomize: !seeded,
            animate: animate,
            animationDuration: 300,
            // Always re-fit. Seeding keeps the nodes where the user last saw them, but
            // without a fit an expansion drops new neighbours outside the viewport, where
            // they read as missing rather than merely off to one side.
            fit: true,
            padding: 40,
            nodeDimensionsIncludeLabels: true,
            // Scaled to the sectioned tiles, which are wider and taller than the old
            // text-sized shapes; anything tighter and the bowed edges thread the gaps.
            idealEdgeLength: 180,
            nodeRepulsion: 9500,
            gravity: 0.25,
            numIter: 2000
        };
    }

    function runLayout(name, seeded) {
        // Only what is on screen takes part: a hidden node left in the layout would
        // reserve empty space in the middle of the picture.
        var visible = cy.elements(":visible");
        if (visible.nodes().length === 0) {
            return;
        }

        visible.layout(layoutOptions(name, seeded)).run();
    }

    // ---- Hidden-link accounting -------------------------------------------

    /*
      What is hidden is simply the stored total minus what is drawn right now. Deriving it
      here rather than trusting a number computed per fragment is what keeps the expand
      buttons truthful after a merge: the host cannot know which neighbours the canvas has
      already picked up from an earlier expansion.
    */
    function updateHiddenCounts() {
        cy.nodes().forEach(function (node) {
            // io links are not symbol references: the stored totals never counted them,
            // so the drawn count must not either, or every stub would eat one badge slot.
            node.data("hiddenCallers", Math.max(0,
                node.data("totalCallers") - node.incomers('edge[kind != "io"]').length));
            node.data("hiddenCallees", Math.max(0,
                node.data("totalCallees") - node.outgoers('edge[kind != "io"]').length));
        });
    }

    // ---- Hiding elements from the view -------------------------------------

    /*
      Hiding is presentation only. Nothing is dropped from the graph model, so the counts
      the expand badges are derived from stay exactly as the host reported them, and one
      button puts everything back. The bar is the only thing on screen that knows anything
      was removed, so it always states how many.
    */
    function updateHiddenBar() {
        var count = cy.elements(".user-hidden").length;
        if (count === 0) {
            elements.hiddenBar.hidden = true;
            return;
        }

        elements.hiddenCount.textContent = count + (count === 1 ? " item hidden" : " items hidden");
        elements.hiddenBar.hidden = false;
    }

    function hideElements(collection) {
        if (collection.length === 0) {
            return;
        }

        // A hidden node's edges would otherwise be left dangling in the picture.
        collection.union(collection.connectedEdges()).addClass("user-hidden");

        // The popover describes something that may have just gone; drop it rather than
        // leave it pointing at empty canvas.
        collection.unselect();
        hidePopover();
        clearHighlight();

        updateHiddenBar();
        runLayout(currentLayout, true);
    }

    function showAllHidden() {
        cy.elements(".user-hidden").removeClass("user-hidden");
        updateHiddenBar();
        runLayout(currentLayout, true);
    }

    elements.showHidden.addEventListener("click", showAllHidden);

    // ---- Right-click menu --------------------------------------------------

    function hideContextMenu() {
        elements.contextMenu.hidden = true;
    }

    function showContextMenu(title, actions, renderedPosition) {
        elements.contextMenuTitle.textContent = title;

        while (elements.contextMenuItems.firstChild) {
            elements.contextMenuItems.removeChild(elements.contextMenuItems.firstChild);
        }

        actions.forEach(function (action) {
            var button = document.createElement("button");
            button.type = "button";
            button.textContent = action.label;
            button.addEventListener("click", function () {
                hideContextMenu();
                action.run();
            });
            elements.contextMenuItems.appendChild(button);
        });

        // Shown before measuring, since a hidden element has no size to clamp against.
        elements.contextMenu.hidden = false;

        var width = elements.contextMenu.offsetWidth;
        var height = elements.contextMenu.offsetHeight;
        var left = Math.min(renderedPosition.x, window.innerWidth - width - 8);
        var top = Math.min(renderedPosition.y, window.innerHeight - height - 8);

        elements.contextMenu.style.left = Math.max(8, left) + "px";
        elements.contextMenu.style.top = Math.max(8, top) + "px";
    }

    cy.on("cxttap", "node", function (event) {
        var node = event.target;
        var label = node.data("container")
            ? node.data("container") + "." + node.data("name")
            : node.data("name");

        showContextMenu(label, [
            { label: "Hide this node", run: function () { hideElements(node); } },
            {
                label: "Hide everything else",
                run: function () {
                    hideElements(cy.nodes().difference(node.closedNeighborhood().nodes()));
                }
            },
            {
                label: "Hide unconnected nodes",
                run: function () {
                    hideElements(cy.nodes().filter(function (other) {
                        return other.connectedEdges(":visible").length === 0;
                    }));
                }
            }
        ], event.renderedPosition);
    });

    cy.on("cxttap", "edge", function (event) {
        var edge = event.target;
        var label = edgeEndpointName(edge, "source") + " → " + edgeEndpointName(edge, "target");

        showContextMenu(label, [
            { label: "Hide this link", run: function () { hideElements(edge); } },
            {
                label: "Hide both ends",
                run: function () { hideElements(edge.connectedNodes()); }
            }
        ], event.renderedPosition);
    });

    cy.on("cxttap", function (event) {
        if (event.target !== cy) {
            return;
        }

        if (cy.elements(".user-hidden").length === 0) {
            hideContextMenu();
            return;
        }

        showContextMenu("Canvas", [
            { label: "Show hidden items", run: showAllHidden }
        ], event.renderedPosition);
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            hideContextMenu();
        }
    });

    // ---- Legend size -------------------------------------------------------

    /*
      Bounds mirror LegendFontSizes in Core. The page clamps as well as the host because
      the value round-trips through both, and a control that can walk past its own limit
      would let one click produce a legend nobody can read.
    */
    var LEGEND_MIN = 9;
    var LEGEND_MAX = 22;
    var LEGEND_STEP = 1.5;
    var legendFontSize = 10.5;

    function applyLegendSize(size) {
        legendFontSize = Math.min(LEGEND_MAX, Math.max(LEGEND_MIN, size));
        document.documentElement.style.setProperty("--legend-font-size", legendFontSize + "px");
        elements.legendSmaller.disabled = legendFontSize <= LEGEND_MIN;
        elements.legendLarger.disabled = legendFontSize >= LEGEND_MAX;
    }

    function stepLegendSize(delta) {
        var before = legendFontSize;
        applyLegendSize(legendFontSize + delta);
        if (legendFontSize !== before) {
            // The host stores it, so the size survives a restart the way the theme does.
            bridge.post("legendSizeChanged", { size: legendFontSize });
        }
    }

    elements.legendSmaller.addEventListener("click", function () { stepLegendSize(-LEGEND_STEP); });
    elements.legendLarger.addEventListener("click", function () { stepLegendSize(LEGEND_STEP); });

    bridge.on("setLegendSize", function (payload) {
        applyLegendSize(Number(payload.size) || legendFontSize);
    });

    applyLegendSize(legendFontSize);

    // ---- Node detail lines -------------------------------------------------

    /*
      Same five-hop shape as the size control above: apply locally, post only when the
      value actually moved (which is what stops the inbound message echoing back), and
      accept the host's value without posting. The host stores it in session.json and
      re-sends it on Ready.
    */
    function applyNodeDetails(show) {
        showNodeDetails = Boolean(show);
        elements.legendDetails.setAttribute("aria-pressed", String(showNodeDetails));
        elements.legendDetails.textContent = showNodeDetails ? "on" : "off";
        retileAll();
        applyStubVisibility();

        // The toggle folds whole tile sections away — and takes the stubs with it — so
        // the sizes the layout placed no longer hold and the picture is re-spaced.
        if (cy.nodes().length > 0) {
            runLayout(currentLayout, true);
        }
    }

    function toggleNodeDetails() {
        applyNodeDetails(!showNodeDetails);
        bridge.post("nodeDetailsChanged", { show: showNodeDetails });
    }

    elements.legendDetails.addEventListener("click", toggleNodeDetails);

    bridge.on("setNodeDetails", function (payload) {
        applyNodeDetails(payload.show);
    });

    applyNodeDetails(showNodeDetails);

    // ---- Popover -----------------------------------------------------------

    function hidePopover() {
        elements.popover.hidden = true;
    }

    /*
      The stub's popover states the whole honesty chain in one line: which way the data
      goes, who said so, and — for gated matches — the co-occurrence rule that admitted a
      generic member name. Site rows arrive in the detail pane, not here.
    */
    function showStubPopover(node) {
        var data = node.data();

        elements.popoverTitle.textContent =
            (DIRECTION_GLYPHS[data.direction] || "") + " " + data.name;

        var parts = [data.directionLabel + " boundary", data.source];
        if (data.gateNote) {
            parts.push(data.gateNote);
        }
        if (data.argText) {
            parts.push(data.argText);
        }
        parts.push(data.siteCount + (data.siteCount === 1 ? " call site" : " call sites"));
        elements.popoverSub.textContent = parts.join("  ·  ");

        elements.expandCallers.hidden = true;
        elements.expandCallees.hidden = true;
        elements.popoverSites.hidden = true;
        elements.popoverSites.textContent = "";

        elements.popover.hidden = false;
        positionPopover(node);
    }

    function showPopover(node) {
        var data = node.data();

        if (data.isIoStub) {
            showStubPopover(node);
            return;
        }

        // textContent, never innerHTML: these strings come out of the user's source.
        elements.popoverTitle.textContent = data.container
            ? data.container + "." + data.name
            : data.name;

        // The descriptor already states the kind, the modifiers and the overload
        // position, so it replaces the first two parts rather than joining them.
        var parts = [data.descriptor || data.kind];
        parts.push(data.path + ":" + data.line);

        // Untruncated here, which is the point of cutting it on the node: the label has a
        // box to fit into and this does not.
        if (data.params) {
            parts.push(data.params);
        }
        if (data.signature) {
            parts.push(data.signature);
        }
        elements.popoverSub.textContent = parts.join("  ·  ");

        configureExpandButton(elements.expandCallers, node, "hiddenCallers", "callers", "caller", "callers");
        configureExpandButton(elements.expandCallees, node, "hiddenCallees", "callees", "dependency", "dependencies");
        elements.popoverSites.hidden = true;
        elements.popoverSites.textContent = "";

        elements.popover.hidden = false;
        positionPopover(node);
    }

    // ---- Edge popover ------------------------------------------------------

    function edgeEndpointName(edge, end) {
        var node = end === "source" ? edge.source() : edge.target();
        return node.length > 0 ? node.data("name") : "?";
    }

    function showEdgePopover(edge) {
        var data = edge.data();

        elements.popoverTitle.textContent =
            edgeEndpointName(edge, "source") + " → " + edgeEndpointName(edge, "target");

        var sites = data.callSites || 1;
        var parts = [data.kind, data.confidenceLabel,
            sites + (sites === 1 ? " call site" : " call sites")];
        if (data.candidates > 1) {
            parts.push("one of " + data.candidates + " name matches");
        }
        elements.popoverSub.textContent = parts.join("  ·  ");

        elements.expandCallers.hidden = true;
        elements.expandCallees.hidden = true;
        elements.popoverSites.textContent = "";
        elements.popoverSites.hidden = true;

        elements.popover.hidden = false;
        positionEdgePopover(edge);

        // The per-site list is fetched on demand rather than shipped with the graph:
        // it is only worth reading once this edge is the one being asked about.
        bridge.post("edgeSelected", {
            source: data.source,
            target: data.target,
            kindId: data.kindId,
            edgeId: data.id
        });
    }

    function positionEdgePopover(edge) {
        if (elements.popover.hidden) {
            return;
        }

        var point = edge.renderedMidpoint();
        var width = elements.popover.offsetWidth;
        var height = elements.popover.offsetHeight;

        var left = point.x - width / 2;
        var top = point.y + 12;
        if (top + height > window.innerHeight - 8) {
            top = point.y - height - 12;
        }

        elements.popover.style.left = Math.max(8, Math.min(left, window.innerWidth - width - 8)) + "px";
        elements.popover.style.top = Math.max(8, top) + "px";
    }

    function selectedEdge() {
        var selected = cy.$("edge:selected");
        return selected.length > 0 ? selected[0] : null;
    }

    function configureExpandButton(button, node, key, direction, singular, pluralForm) {
        var hidden = node.data(key) || 0;
        if (hidden > 0) {
            button.textContent = "Show " + hidden + " more " + (hidden === 1 ? singular : pluralForm);
            button.dataset.direction = direction;
            button.hidden = false;
        } else {
            button.hidden = true;
        }
    }

    function positionPopover(node) {
        if (elements.popover.hidden) {
            return;
        }

        var point = node.renderedPosition();
        var box = node.renderedBoundingBox();
        var width = elements.popover.offsetWidth;
        var height = elements.popover.offsetHeight;

        var left = point.x - width / 2;
        var top = box.y2 + 10;

        // Flip above the node when there is no room below it.
        if (top + height > window.innerHeight - 8) {
            top = box.y1 - height - 10;
        }

        elements.popover.style.left = Math.max(8, Math.min(left, window.innerWidth - width - 8)) + "px";
        elements.popover.style.top = Math.max(8, top) + "px";
    }

    function selectedNode() {
        var selected = cy.$("node:selected");
        return selected.length > 0 ? selected[0] : null;
    }

    function refreshPopover() {
        var node = selectedNode();
        if (node) {
            positionPopover(node);
            return;
        }

        var edge = selectedEdge();
        if (edge) {
            positionEdgePopover(edge);
        } else {
            hidePopover();
        }
    }

    // ---- Highlighting ------------------------------------------------------

    function highlightNeighbourhood(node) {
        var keep = node.closedNeighborhood();
        cy.elements().difference(keep).addClass("dimmed");
        keep.removeClass("dimmed");
    }

    function clearHighlight() {
        cy.elements().removeClass("dimmed");
    }

    // ---- Interaction -------------------------------------------------------

    cy.on("tap", "node", function (event) {
        var node = event.target;
        var id = node.id();
        var now = Date.now();

        // A stub is not a symbol: its id must never reach the symbol paths, and there is
        // nothing to re-root on. Clicking it asks the host for the per-site facts.
        if (node.data("isIoStub")) {
            lastTap = { id: null, at: 0 };
            highlightNeighbourhood(node);
            showPopover(node);
            bridge.post("ioStubSelected", {
                name: node.data("name"),
                directionLabel: node.data("directionLabel"),
                source: node.data("source"),
                gateNote: node.data("gateNote") || null,
                refIds: node.data("refIds")
            });
            return;
        }

        // Rolled by hand rather than relying on a dbltap event, so the behaviour does not
        // depend on which build of the renderer is bundled.
        if (lastTap.id === id && now - lastTap.at < DOUBLE_TAP_MS) {
            lastTap = { id: null, at: 0 };
            bridge.post("nodeActivated", { id: id });
            return;
        }

        lastTap = { id: id, at: now };

        highlightNeighbourhood(node);
        showPopover(node);
        bridge.post("nodeSelected", { id: id });
    });

    cy.on("tap", "edge", function (event) {
        var edge = event.target;
        var id = edge.id();
        var now = Date.now();

        // An io link is half of its stub: clicking it means the stub, and the edge-detail
        // query has no rows to answer for it anyway.
        if (edge.data("kind") === "io") {
            var stub = edge.connectedNodes("[?isIoStub]");
            if (stub.length > 0) {
                stub.emit("tap");
            }
            return;
        }

        // Double-tap on an edge jumps the preview to its first call site.
        if (lastTap.id === id && now - lastTap.at < DOUBLE_TAP_MS) {
            lastTap = { id: null, at: 0 };
            bridge.post("edgeActivated", {
                source: edge.data("source"),
                line: edge.data("line")
            });
            return;
        }

        lastTap = { id: id, at: now };

        // Same dim gesture as a node click: the edge and its two endpoints stay lit.
        var keep = edge.union(edge.connectedNodes());
        cy.elements().difference(keep).addClass("dimmed");
        keep.removeClass("dimmed");

        showEdgePopover(edge);
    });

    cy.on("tap", function (event) {
        hideContextMenu();

        if (event.target === cy) {
            lastTap = { id: null, at: 0 };
            clearHighlight();
            hidePopover();
        }
    });

    cy.on("pan zoom position", function () {
        // The menu is anchored to a screen point, not to the element, so it would drift
        // off the thing it names as soon as the canvas moves.
        hideContextMenu();
        refreshPopover();
    });

    window.addEventListener("resize", refreshPopover);

    function requestExpand(event) {
        var node = selectedNode();
        if (!node) {
            return;
        }

        bridge.post("expandRequested", {
            id: node.id(),
            direction: event.currentTarget.dataset.direction
        });
    }

    elements.expandCallers.addEventListener("click", requestExpand);
    elements.expandCallees.addEventListener("click", requestExpand);

    // ---- Host messages -----------------------------------------------------

    function setOverlay(message) {
        if (message) {
            elements.overlayText.textContent = message;
            elements.overlay.hidden = false;
            elements.legend.hidden = true;
        } else {
            elements.overlay.hidden = true;
            elements.legend.hidden = false;
        }
    }

    bridge.on("setGraph", function (payload) {
        var graph = payload.graph || {};
        currentLayout = payload.layout || currentLayout;

        hidePopover();
        hideContextMenu();

        // A new fragment is a new picture: what the user chose to hide out of the last
        // one says nothing about this one, and carrying it over would silently drop
        // nodes the host just sent.
        cy.elements().remove();
        updateHiddenBar();

        var added = toElements(graph);
        if (added.length === 0) {
            setOverlay("No indexed facts for this symbol.");
            elements.truncation.hidden = true;
            return;
        }

        setOverlay(null);
        cy.add(added);
        assignEdgeBows();
        updateHiddenCounts();
        applyStubVisibility();
        elements.truncation.hidden = !graph.truncated;

        runLayout(currentLayout, false);

        var focus = graph.focusId ? cy.getElementById(graph.focusId) : null;
        if (focus && focus.length > 0) {
            focus.select();
        }
    });

    bridge.on("mergeGraph", function (payload) {
        var graph = payload.graph || {};
        var anchor = cy.getElementById(payload.expandedId);
        if (anchor.length === 0) {
            return;
        }

        // The expanded node keeps its focus ring only if the host still says it is the
        // focus, so an expansion never silently re-roots the graph.
        var fresh = toElements(graph).filter(function (element) {
            return cy.getElementById(element.data.id).length === 0;
        });

        var origin = anchor.position();
        fresh.forEach(function (element) {
            if (element.group === "nodes") {
                // Only the host decides what the focus is; an expansion must not re-root
                // the graph underneath the user.
                element.data.isFocus = false;

                // Seed new nodes at the node they came from so the layout grows outward
                // from there instead of dropping them in at random.
                element.position = {
                    x: origin.x + (Math.random() - 0.5) * 60,
                    y: origin.y + (Math.random() - 0.5) * 60
                };
            }
        });

        var addedNodes = false;
        if (fresh.length > 0) {
            cy.add(fresh);
            // Over every edge, not just the fresh ones: a new parallel link changes
            // which way its older sibling has to bow.
            assignEdgeBows();
            addedNodes = fresh.some(function (element) {
                return element.group === "nodes";
            });
        }

        updateHiddenCounts();
        applyStubVisibility();

        if (graph.truncated) {
            elements.truncation.hidden = false;
        }

        if (addedNodes) {
            runLayout(currentLayout, true);
        }

        var selected = selectedNode();
        if (selected) {
            highlightNeighbourhood(selected);
            showPopover(selected);
        }
    });

    bridge.on("edgeDetails", function (payload) {
        var edge = selectedEdge();
        if (!edge || edge.id() !== payload.edgeId) {
            // The user moved on before the answer arrived; showing it now would attach
            // one edge's call sites to another's popover.
            return;
        }

        var sites = payload.sites || [];
        if (sites.length === 0) {
            return;
        }

        elements.popoverSites.textContent = "";
        sites.forEach(function (site) {
            var row = document.createElement("button");
            row.type = "button";
            row.className = "popover-site";

            var lineSpan = document.createElement("span");
            lineSpan.className = "site-line";
            lineSpan.textContent = ":" + site.line;
            row.appendChild(lineSpan);

            // textContent path only: argument text is a verbatim slice of user source.
            row.appendChild(document.createTextNode(site.argText || ""));

            row.addEventListener("click", function () {
                bridge.post("edgeActivated", { source: edge.data("source"), line: site.line });
            });

            elements.popoverSites.appendChild(row);
        });

        elements.popoverSites.hidden = false;
        positionEdgePopover(edge);
    });

    bridge.on("setLayout", function (payload) {
        currentLayout = payload.layout || "force";
        runLayout(currentLayout, false);
    });

    bridge.on("focus", function (payload) {
        var node = cy.getElementById(payload.id);
        if (node.length === 0) {
            return;
        }

        // Being asked to focus something the user hid outranks the hiding: centring on an
        // invisible node would look like the command did nothing.
        if (node.hasClass("user-hidden")) {
            node.removeClass("user-hidden");
            updateHiddenBar();
        }

        cy.$("node:selected").unselect();
        node.select();
        cy.animate({ center: { eles: node }, duration: 220 });
        highlightNeighbourhood(node);
        showPopover(node);
    });

    /*
      Export answers with exactly what is on the canvas — including everything pulled in
      by expansions, and excluding anything hidden from the right-click menu — because
      that is the picture the user is looking at. The JSON carries only indexed facts;
      layout positions and labels are presentation, not facts, and are deliberately left
      out.
    */
    bridge.on("exportView", function (payload) {
        var format = payload.format === "json" ? "json" : "png";

        if (cy.nodes(":visible").length === 0) {
            bridge.post("exportResult", { format: format, data: null });
            return;
        }

        if (format === "png") {
            bridge.post("exportResult", {
                format: "png",
                // full:true renders the whole graph regardless of pan/zoom; scale 2 keeps
                // node text readable when the image is pasted somewhere larger.
                data: cy.png({ full: true, scale: 2, bg: cssVar("--bg") })
            });
            return;
        }

        var doc = {
            nodes: cy.nodes(":visible").map(function (node) {
                var d = node.data();
                var entry = {
                    id: d.id,
                    name: d.name,
                    kind: d.kind,
                    path: d.path,
                    line: d.line
                };
                if (d.value) { entry.value = d.value; }
                if (d.signature) { entry.signature = d.signature; }
                if (d.params) { entry.params = d.params; }
                if (d.modifiers) { entry.modifiers = d.modifiers; }
                if (d.container) { entry.container = d.container; }
                if (d.isFocus) { entry.isFocus = true; }
                if (d.isIoStub) {
                    // The direction is not a source fact, so the export names its source.
                    entry.ioBoundary = { direction: d.directionLabel, source: d.source };
                    if (d.argText) { entry.argText = d.argText; }
                    delete entry.path;
                    delete entry.line;
                }
                return entry;
            }),
            edges: cy.edges(":visible").map(function (edge) {
                var d = edge.data();
                var entry = {
                    source: d.source,
                    target: d.target,
                    kind: d.kind,
                    confidence: d.confidence,
                    line: d.line
                };
                if (d.candidates > 1) { entry.candidates = d.candidates; }
                if (d.callSites > 1) { entry.callSites = d.callSites; }
                return entry;
            })
        };

        bridge.post("exportResult", { format: "json", data: JSON.stringify(doc, null, 2) });
    });

    bridge.on("clear", function (payload) {
        cy.elements().remove();
        hidePopover();
        hideContextMenu();
        updateHiddenBar();
        elements.truncation.hidden = true;
        setOverlay(payload.message || "");
    });

    setOverlay("Search for a symbol to see its dependency graph.");

    window.viewHost.register("graph", {
        element: document.getElementById("view-graph"),
        onShow: function () {
            // Laid out while hidden, cytoscape measures a zero-sized container and parks
            // everything at the origin. Re-measure and re-fit on the way back in.
            cy.resize();
            if (cy.nodes().length > 0) {
                cy.fit(undefined, 40);
            }
            refreshPopover();
        },
        onHide: function () {
            hidePopover();
            hideContextMenu();
        },
        onResize: function () {
            cy.resize();
            refreshPopover();
        },
        onTheme: applyTheme
    });
})();

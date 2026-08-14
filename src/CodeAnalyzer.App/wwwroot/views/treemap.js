/*
  Dependency treemap.

  One level at a time: the host sends the children of a path, never the whole tree, so
  drilling into a 20k-file workspace costs the same as drilling into a small one.

  Tile area is the count of indexed symbols (source lines, once the drill reaches inside a
  single file). Tile colour is the share of that tile's resolved references that leave it —
  a directory that only talks to itself sits at one end of the scale and a directory that
  is mostly glue sits at the other. Both numbers are counts from the index; neither is a
  score anyone invented.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-treemap"),
        crumbs: document.getElementById("treemap-crumbs"),
        plot: document.getElementById("treemap"),
        tip: document.getElementById("treemap-tip"),
        legend: document.getElementById("treemap-legend"),
        empty: document.getElementById("treemap-empty")
    };

    var lastPayload = null;

    /* Below this a label is unreadable and only adds clutter. */
    var LABEL_MIN_WIDTH = 54;
    var LABEL_MIN_HEIGHT = 22;

    function setEmpty(message) {
        util.overlay(elements.empty, message);
        elements.legend.hidden = Boolean(message);
    }

    // ---- Breadcrumbs -------------------------------------------------------

    function renderCrumbs(path) {
        util.clear(elements.crumbs);

        var crumb = el("button", "crumb", "workspace");
        crumb.type = "button";
        crumb.dataset.path = "";
        elements.crumbs.appendChild(crumb);

        if (!path) {
            crumb.classList.add("crumb-current");
            return;
        }

        var segments = path.split("/");
        var accumulated = "";

        segments.forEach(function (segment, index) {
            accumulated = accumulated ? accumulated + "/" + segment : segment;

            elements.crumbs.appendChild(el("span", "crumb-sep", "/"));

            var next = el("button", "crumb", segment);
            next.type = "button";
            next.dataset.path = accumulated;
            if (index === segments.length - 1) {
                next.classList.add("crumb-current");
            }
            elements.crumbs.appendChild(next);
        });
    }

    elements.crumbs.addEventListener("click", function (event) {
        var crumb = event.target.closest(".crumb");
        if (crumb && !crumb.classList.contains("crumb-current")) {
            bridge.post("treemapDrill", { path: crumb.dataset.path });
        }
    });

    // ---- Colour ------------------------------------------------------------

    /*
      Share of a tile's resolved references that cross its own boundary. Undefined when the
      tile has no references at all, and an undefined value is drawn in a neutral colour
      rather than at either end of the scale: no data is not the same as zero.
    */
    function outwardShare(tile) {
        var total = tile.internalLinks + tile.outgoingLinks;
        return total === 0 ? null : tile.outgoingLinks / total;
    }

    function colourFor(tile) {
        var share = outwardShare(tile);
        if (share === null) {
            return util.cssVar("--control");
        }

        return d3.interpolateLab(
            util.cssVar("--kind-module"),
            util.cssVar("--kind-macro"))(share);
    }

    /*
      Tiles with no references are drawn in the neutral panel colour, which is dark in the
      dark theme and light in the light one, while the scale colours are mid-tone in both.
      A single fixed label colour is unreadable on one of those, so pick per tile.
    */
    function labelColour(fill) {
        var rgb = d3.rgb(fill);
        var luminance = (0.299 * rgb.r + 0.587 * rgb.g + 0.114 * rgb.b) / 255;
        return luminance > 0.55 ? "#10121a" : "#f2f4f8";
    }

    // ---- Drawing -----------------------------------------------------------

    function render(payload) {
        lastPayload = payload;

        if (!payload) {
            setEmpty("Nothing indexed here yet.");
            return;
        }

        renderCrumbs(payload.path);

        var tiles = (payload.tiles || []).filter(function (tile) {
            return tile.value > 0;
        });

        if (tiles.length === 0) {
            util.clear(elements.plot);
            setEmpty(payload.path
                ? "Nothing indexed under " + payload.path + "."
                : "Nothing indexed yet. Select directories and index them first.");
            return;
        }

        setEmpty(null);
        draw(tiles, payload);
    }

    function draw(tiles, payload) {
        util.clear(elements.plot);
        hideTip();

        var width = elements.plot.clientWidth;
        var height = elements.plot.clientHeight;
        if (width < 10 || height < 10) {
            // Laid out while hidden. onShow redraws once the real size is known.
            return;
        }

        var root = d3.hierarchy({ children: tiles })
            .sum(function (node) {
                return node.value || 0;
            })
            .sort(function (a, b) {
                return b.value - a.value;
            });

        d3.treemap()
            .tile(d3.treemapSquarify)
            .size([width, height])
            .paddingInner(2)
            .round(true)(root);

        var svg = d3.select(elements.plot)
            .append("svg")
            .attr("width", width)
            .attr("height", height)
            .attr("viewBox", "0 0 " + width + " " + height);

        var cell = svg.selectAll("g")
            .data(root.leaves())
            .join("g")
            .attr("transform", function (node) {
                return "translate(" + node.x0 + "," + node.y0 + ")";
            });

        cell.append("rect")
            .attr("class", "tile")
            .attr("width", function (node) {
                return node.x1 - node.x0;
            })
            .attr("height", function (node) {
                return node.y1 - node.y0;
            })
            .style("fill", function (node) {
                return colourFor(node.data);
            })
            .style("stroke", util.cssVar("--panel"));

        // Labels are appended only where they fit; SVG has no ellipsis, and a name spilling
        // across three neighbouring tiles is worse than no name.
        cell.each(function (node) {
            var boxWidth = node.x1 - node.x0;
            var boxHeight = node.y1 - node.y0;
            if (boxWidth < LABEL_MIN_WIDTH || boxHeight < LABEL_MIN_HEIGHT) {
                return;
            }

            var group = d3.select(this);
            var characters = Math.max(3, Math.floor((boxWidth - 10) / 6.2));
            var ink = labelColour(colourFor(node.data));

            group.append("text")
                .attr("class", "tile-label")
                .attr("x", 6)
                .attr("y", 15)
                .style("fill", ink)
                .text(util.truncate(node.data.name, characters));

            if (boxHeight >= 38) {
                group.append("text")
                    .attr("class", "tile-sub")
                    .attr("x", 6)
                    .attr("y", 28)
                    .style("fill", ink)
                    .text(util.truncate(secondLine(node.data), characters));
            }
        });

        cell.on("mousemove", function (event, node) {
                showTip(event, node.data);
            })
            .on("mouseleave", hideTip)
            .on("click", function (event, node) {
                activate(node.data, payload);
            });
    }

    function secondLine(tile) {
        if (tile.type === "symbol") {
            return tile.kind;
        }

        if (tile.type === "file") {
            return util.count(tile.symbols, "symbol", "symbols");
        }

        return util.count(tile.files, "file", "files");
    }

    function activate(tile, payload) {
        if (tile.type === "symbol") {
            // The leaf level is the one place a tile is a symbol, so clicking it can do the
            // most useful thing: make it the subject everywhere else.
            if (tile.symbolId) {
                bridge.post("nodeActivated", { id: tile.symbolId });
            }
            return;
        }

        bridge.post("treemapDrill", { path: tile.path, from: payload.path });
    }

    // ---- Tooltip -----------------------------------------------------------

    function showTip(event, tile) {
        util.clear(elements.tip);

        elements.tip.appendChild(el("div", "tip-title", tile.name));

        var facts = [];
        if (tile.type === "symbol") {
            facts.push(tile.kind);
            facts.push(util.count(tile.value, "line", "lines"));
        } else {
            if (tile.type === "dir") {
                facts.push(util.count(tile.files, "file", "files"));
            }
            facts.push(util.count(tile.symbols, "symbol", "symbols"));
        }

        var share = outwardShare(tile);
        if (share === null) {
            facts.push("no resolved references");
        } else {
            facts.push(Math.round(share * 100) + "% of " +
                util.count(tile.internalLinks + tile.outgoingLinks, "reference", "references") +
                " leave it");
        }

        facts.forEach(function (fact) {
            elements.tip.appendChild(el("div", "tip-line", fact));
        });

        elements.tip.appendChild(el("div", "tip-hint",
            tile.type === "symbol" ? "click to focus" : "click to open"));

        elements.tip.hidden = false;

        var bounds = elements.plot.getBoundingClientRect();
        var left = event.clientX - bounds.left + 14;
        var top = event.clientY - bounds.top + 14;

        if (left + elements.tip.offsetWidth > bounds.width) {
            left = event.clientX - bounds.left - elements.tip.offsetWidth - 10;
        }
        if (top + elements.tip.offsetHeight > bounds.height) {
            top = event.clientY - bounds.top - elements.tip.offsetHeight - 10;
        }

        elements.tip.style.left = Math.max(0, left) + "px";
        elements.tip.style.top = Math.max(0, top) + "px";
    }

    function hideTip() {
        elements.tip.hidden = true;
    }

    // ---- Host messages -----------------------------------------------------

    bridge.on("setTreemap", function (payload) {
        render(payload.treemap || null);
    });

    window.viewHost.register("treemap", {
        element: elements.section,
        onShow: function () {
            render(lastPayload);
        },
        onHide: hideTip,
        onResize: function () {
            render(lastPayload);
        },
        onTheme: function () {
            render(lastPayload);
        }
    });
})();

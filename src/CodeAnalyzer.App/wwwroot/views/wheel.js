/*
  Import / include wheel.

  Top-level directories around a ring, one ribbon per dependency direction between them.
  It answers a question the other four views cannot: which parts of this workspace lean on
  which, at a glance and without picking a symbol first.

  A ribbon is a count of resolved links and nothing else. Dependencies that name something
  outside the workspace have no second end to draw, so they are reported as a number on the
  arc instead of being drawn as a stub — a stub would look like a link to somewhere.
*/
(function () {
    "use strict";

    var bridge = window.graphBridge;
    var util = window.viewUtil;
    var el = util.el;

    var elements = {
        section: document.getElementById("view-wheel"),
        plot: document.getElementById("wheel"),
        tip: document.getElementById("wheel-tip"),
        note: document.getElementById("wheel-note"),
        empty: document.getElementById("wheel-empty")
    };

    var lastPayload = null;

    /* Leaves room for the labels that sit outside the ring. */
    var LABEL_MARGIN = 128;

    function setEmpty(message) {
        util.overlay(elements.empty, message);
    }

    // ---- Colour ------------------------------------------------------------

    /*
      A generated hue ramp rather than a fixed palette: the number of top-level directories
      is whatever the workspace has. Lightness is picked per theme so the arcs stay legible
      on both backgrounds.
    */
    function arcColours(count) {
        var light = document.documentElement.getAttribute("data-theme") === "light";
        var colours = [];

        for (var i = 0; i < count; i++) {
            var hue = Math.round((360 * i) / Math.max(1, count));
            colours.push(d3.hsl(hue, light ? 0.55 : 0.5, light ? 0.44 : 0.62).formatHex());
        }

        return colours;
    }

    // ---- Drawing -----------------------------------------------------------

    function render(payload) {
        lastPayload = payload;
        util.clear(elements.plot);
        hideTip();

        if (!payload) {
            elements.note.textContent = "";
            setEmpty("No dependencies to draw.");
            return;
        }

        renderNote(payload);

        var groups = payload.groups || [];
        var links = payload.links || [];

        if (groups.length === 0 || links.length === 0) {
            setEmpty(emptyReason(payload));
            return;
        }

        setEmpty(null);
        draw(payload, groups, links);
    }

    function emptyReason(payload) {
        if (payload.source === "includes") {
            // The C# case, and it deserves an explanation rather than a blank circle.
            return "No include or import line in this workspace resolves to another indexed " +
                "file. Some languages name namespaces rather than files — switch the wheel to " +
                "symbol references to see the dependencies those workspaces do have.";
        }

        return "No resolved references cross between top-level directories.";
    }

    function renderNote(payload) {
        util.clear(elements.note);
        elements.note.appendChild(el("span", null, payload.sourceLabel));

        if (payload.omittedGroups > 0) {
            elements.note.appendChild(el("span", "dot", "·"));
            elements.note.appendChild(el("span", "warn",
                payload.omittedGroups + " smaller " +
                (payload.omittedGroups === 1 ? "directory is" : "directories are") + " not shown"));
        }
    }

    function draw(payload, groups, links) {
        var width = elements.plot.clientWidth;
        var height = elements.plot.clientHeight;
        if (width < 60 || height < 60) {
            return;
        }

        var size = Math.min(width, height);
        var outer = Math.max(40, size / 2 - LABEL_MARGIN / 2);
        var inner = outer - 12;

        var index = Object.create(null);
        groups.forEach(function (group, i) {
            index[group.id] = i;
        });

        var matrix = groups.map(function () {
            return groups.map(function () {
                return 0;
            });
        });

        links.forEach(function (link) {
            var from = index[link.source];
            var to = index[link.target];
            if (from !== undefined && to !== undefined) {
                matrix[from][to] += link.count;
            }
        });

        var chords = d3.chord()
            .padAngle(0.045)
            .sortSubgroups(d3.descending)(matrix);

        var colours = arcColours(groups.length);

        var svg = d3.select(elements.plot)
            .append("svg")
            .attr("width", width)
            .attr("height", height)
            .attr("viewBox", [-width / 2, -height / 2, width, height].join(" "));

        var arc = d3.arc().innerRadius(inner).outerRadius(outer);
        var ribbon = d3.ribbon().radius(inner);

        var ribbons = svg.append("g")
            .attr("class", "ribbons")
            .selectAll("path")
            .data(chords)
            .join("path")
            .attr("class", "ribbon")
            .attr("d", ribbon)
            .style("fill", function (chord) {
                return colours[chord.source.index];
            })
            .style("stroke", util.cssVar("--panel"));

        var arcs = svg.append("g")
            .selectAll("g")
            .data(chords.groups)
            .join("g");

        arcs.append("path")
            .attr("class", "wheel-arc")
            .attr("d", arc)
            .style("fill", function (group) {
                return colours[group.index];
            })
            .style("stroke", util.cssVar("--panel"));

        arcs.append("text")
            .attr("class", "wheel-label")
            .attr("dy", "0.35em")
            .attr("transform", function (group) {
                var angle = (group.startAngle + group.endAngle) / 2;
                var flip = angle > Math.PI;
                return "rotate(" + ((angle * 180) / Math.PI - 90) + ")" +
                    "translate(" + (outer + 8) + ")" +
                    (flip ? "rotate(180)" : "");
            })
            .attr("text-anchor", function (group) {
                return (group.startAngle + group.endAngle) / 2 > Math.PI ? "end" : "start";
            })
            .style("fill", util.cssVar("--fg-muted"))
            .text(function (group) {
                return util.truncate(groups[group.index].label, 18);
            });

        // Hovering an arc keeps only the ribbons that touch it, which is the only way to
        // read a busy wheel.
        arcs.on("mousemove", function (event, group) {
                ribbons.classed("faded", function (chord) {
                    return chord.source.index !== group.index && chord.target.index !== group.index;
                });
                showGroupTip(event, groups[group.index], payload);
            })
            .on("mouseleave", function () {
                ribbons.classed("faded", false);
                hideTip();
            })
            .on("click", function (event, group) {
                // The wheel says which directory is busy; the treemap says what is in it.
                bridge.post("treemapDrill", { path: groups[group.index].id });
            });

        ribbons.on("mousemove", function (event, chord) {
                showRibbonTip(event, chord, groups, payload);
            })
            .on("mouseleave", hideTip);
    }

    // ---- Tooltip -----------------------------------------------------------

    function showGroupTip(event, group, payload) {
        var lines = [
            util.count(group.files, "file", "files"),
            util.count(group.links, "link", "links") + " on the wheel"
        ];

        if (group.unresolved > 0) {
            lines.push(util.count(group.unresolved, "dependency", "dependencies") +
                " outside this workspace");
        }

        lines.push("click to open in the treemap");
        showTip(event, group.label, lines);
    }

    function showRibbonTip(event, chord, groups, payload) {
        var from = groups[chord.source.index];
        var to = groups[chord.target.index];
        var noun = payload.source === "includes" ? "include" : "reference";

        var lines = [];
        if (chord.source.index === chord.target.index) {
            lines.push(util.count(chord.source.value, noun, noun + "s") + " within it");
            showTip(event, from.label, lines);
            return;
        }

        lines.push(from.label + " → " + to.label + ": " +
            util.count(chord.source.value, noun, noun + "s"));

        if (chord.target.value > 0) {
            lines.push(to.label + " → " + from.label + ": " +
                util.count(chord.target.value, noun, noun + "s"));
        }

        showTip(event, from.label + " ↔ " + to.label, lines);
    }

    function showTip(event, title, lines) {
        util.clear(elements.tip);
        elements.tip.appendChild(el("div", "tip-title", title));
        lines.forEach(function (line) {
            elements.tip.appendChild(el("div", "tip-line", line));
        });
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

    bridge.on("setWheel", function (payload) {
        render(payload.wheel || null);
    });

    window.viewHost.register("wheel", {
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

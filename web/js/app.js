/* NCUT Market — application shell: routing, views, rendering.
 *
 * Split from api.js because jQuery makes every concern wordier; the sibling project keeps all of
 * this in one 921-line file, which is fine with template strings and native fetch and unpleasant
 * with jQuery. Classic script, one IIFE, no module system — there is no build step here.
 */
(function ($, NcutApi) {
  "use strict";

  /* ======================================================================
     State
     ====================================================================== */

  // One page of categories, fetched at the maximum page size. See renderCategories for why the
  // category page does not page.
  var CATEGORY_PAGE_SIZE = 100;

  var DEFAULT_PAGE_SIZE = 20;

  var THEME_KEY = "ncut-market.theme";

  var state = {
    health: null,
    categories: null,
    dormitory: { page: 1, pageSize: DEFAULT_PAGE_SIZE, result: null },
    lookup: null
  };

  /* ======================================================================
     Formatting
     ====================================================================== */

  /**
   * Renders an API timestamp for display.
   *
   * The API sends a wall-clock reading in Beijing time with no offset suffix —
   * "2026-09-29T13:43:09.123". Constructing a Date from that is a trap: per the ES spec, a
   * date-TIME form without an offset is interpreted as the *viewer's local* time, whereas a
   * date-ONLY form is interpreted as UTC. So `new Date(value).toLocaleString()` renders a wrong
   * time on any machine that is not UTC+8 — and renders a *correct* one on the development
   * machine, which is exactly what makes the bug survive testing.
   *
   * The string is already the reading we want to show, so it is reformatted as text and no Date is
   * ever constructed. The real fix is a JsonConverter on the API emitting "+08:00"; that is out of
   * scope while this phase is frontend-only.
   */
  function formatDateTime(value) {
    var match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(String(value == null ? "" : value));

    return match
      ? match[1] + "-" + match[2] + "-" + match[3] + " " + match[4] + ":" + match[5]
      : "—";
  }

  /**
   * Builds one table cell.
   *
   * Accepts a string — which goes through .text() and can never be parsed as HTML — or an
   * already-constructed jQuery object or DOM node, which is appended as-is. There is deliberately
   * no third branch, so "forgot to escape the server value" is not a mistake the shape permits.
   *
   * Note this is the reverse of the sibling project's rule, which is template strings plus an esc()
   * on every interpolation. With jQuery the safe path (.text()) is the one you type by default and
   * .html() has to be reached for on purpose; with template strings it is the other way round, and
   * the whole scheme rests on remembering a wrapper every single time.
   */
  function cell(value) {
    if (value instanceof $ || value instanceof Node) {
      return $("<td>").append(value);
    }

    return $("<td>").text(value == null || value === "" ? "—" : String(value));
  }

  /* ======================================================================
     Shared components
     ====================================================================== */

  function loading() {
    return $("<div>").addClass("loading").text("加载中…");
  }

  /**
   * One "label: value" pair for the error card. The label is a local literal, but the value comes
   * from the server, so it is set with .text() and never concatenated into markup.
   */
  function metaLine(label, value) {
    return $("<span>").text(label + ": ").append($("<code>").text(value));
  }

  /**
   * The inline failure card. Retry re-runs the request rather than reloading the page, because a
   * reload would throw away the page number and filters the user had already chosen.
   */
  function errorCard(error, retryAction) {
    var $card = $("<div>").addClass("error-card");

    $card.append($("<h3>").text("加载失败"));
    $card.append($("<p>").addClass("error-message").text(error.message));

    // code and traceId are shown because traceId is the only thing that ties what the user is
    // looking at to a line in the server log. Hiding it behind a console.log would make the error
    // report impossible to act on.
    var $meta = $("<div>").addClass("error-meta");

    if (error.code) {
      $meta.append(metaLine("code", error.code));
    }

    if (error.traceId) {
      $meta.append(metaLine("traceId", error.traceId));
    }

    if ($meta.children().length) {
      $card.append($meta);
    }

    if (retryAction) {
      $card.append(
        $("<button>").attr({ type: "button", "data-action": retryAction }).addClass("button").text("重试")
      );
    }

    return $card;
  }

  function emptyState(message) {
    return $("<div>").addClass("empty").text(message);
  }

  /**
   * Pager. Both buttons stay rendered and are disabled at the boundaries rather than hidden, so the
   * layout does not shift as the user moves between pages.
   */
  function pager(page, totalPages, totalCount) {
    var $pager = $("<div>").addClass("pager");

    var status = totalCount === 0
      ? "共 0 条"
      : "共 " + totalCount + " 条 · 第 " + page + "/" + totalPages + " 页";

    $pager.append($("<span>").addClass("pager-status").text(status));

    var $actions = $("<div>").addClass("pager-actions");

    $actions.append(
      $("<button>")
        .attr({ type: "button", "data-action": "page", "data-page": page - 1, "data-target": "dormitory" })
        .addClass("button")
        .prop("disabled", page <= 1)
        .text("上一页")
    );

    $actions.append(
      $("<button>")
        .attr({ type: "button", "data-action": "page", "data-page": page + 1, "data-target": "dormitory" })
        .addClass("button")
        .prop("disabled", page >= totalPages)
        .text("下一页")
    );

    $pager.append($actions);

    return $pager;
  }

  function notice(message) {
    return $("<div>").addClass("notice").append($("<p>").text(message));
  }

  function toast(message, kind) {
    var $toast = $("<div>")
      .addClass("toast")
      .addClass(kind === "error" ? "toast-error" : "toast-ok")
      .text(message);

    $("#toast").append($toast);

    // Each toast clears itself. A single shared timer would let a second message cancel the first
    // one's dismissal and leave it on screen forever.
    window.setTimeout(function () {
      $toast.fadeOut(150, function () {
        $toast.remove();
      });
    }, 4000);
  }

  /* ======================================================================
     Category tree
     ====================================================================== */

  /**
   * Rebuilds the tree from the flat list.
   *
   * Two cases need care, and both would otherwise lose rows silently:
   *
   *  - Orphan. A category whose parent has been deactivated still returns, with a parentId that is
   *    not in the result set. Attaching it to nothing is not a shape, and dropping it hides a
   *    category that genuinely exists — so it is promoted to a root.
   *  - Cycle. A parentId loop (A→B→A) satisfies both branches of the pass below while leaving A and
   *    B unreachable from any root. The reachability sweep catches that and promotes them too.
   */
  function buildTree(items) {
    var byId = Object.create(null);
    var childrenOf = Object.create(null);
    var roots = [];
    var i;

    for (i = 0; i < items.length; i++) {
      byId[items[i].id] = items[i];
    }

    for (i = 0; i < items.length; i++) {
      var item = items[i];

      if (item.parentId != null && byId[item.parentId]) {
        if (!childrenOf[item.parentId]) {
          childrenOf[item.parentId] = [];
        }

        childrenOf[item.parentId].push(item);
      } else {
        roots.push(item);
      }
    }

    var reached = Object.create(null);
    var stack = roots.slice();

    while (stack.length) {
      var node = stack.pop();

      if (reached[node.id]) {
        continue;
      }

      reached[node.id] = true;

      var kids = childrenOf[node.id];

      if (kids) {
        for (i = 0; i < kids.length; i++) {
          stack.push(kids[i]);
        }
      }
    }

    for (i = 0; i < items.length; i++) {
      if (!reached[items[i].id]) {
        roots.push(items[i]);
      }
    }

    // Promotion alone would render a cycled node twice — once as its own root, once as a child of
    // the node it points at — because the back-edge is still sitting in childrenOf. Dropping the
    // promoted node out of its parent's child list makes "every category renders exactly once" a
    // property of the structure rather than something the renderer has to keep re-checking.
    //
    // The renderer's ancestor guard stays regardless: it is what stops the walk if this cleanup is
    // ever wrong, and a duplicated row is a far better failure than a hung tab.
    for (i = 0; i < items.length; i++) {
      var promoted = items[i];

      if (reached[promoted.id] || promoted.parentId == null) {
        continue;
      }

      var siblings = childrenOf[promoted.parentId];

      if (siblings) {
        childrenOf[promoted.parentId] = siblings.filter(function (candidate) {
          return candidate.id !== promoted.id;
        });
      }
    }

    return { roots: roots, childrenOf: childrenOf };
  }

  /**
   * Renders one level of the tree.
   *
   * `ancestors` is a prototype-chained set of the ids on the path from the root to here. A child
   * that is already an ancestor is a cycle back-edge and is skipped — it is not lost, because
   * buildTree already promoted it to a root of its own. Without this guard a cycle would recurse
   * until the tab dies.
   *
   * No SVG is built here. jQuery creates elements with document.createElement, which puts an <svg>
   * in the HTML namespace where it renders as nothing; the icon sprite in index.html is safe only
   * because the HTML parser assigns the SVG namespace itself.
   */
  function treeList(nodes, childrenOf, ancestors, depth) {
    var $list = $("<ul>");

    if (depth === 0) {
      $list.addClass("tree");
    }

    for (var i = 0; i < nodes.length; i++) {
      var node = nodes[i];

      if (ancestors[node.id]) {
        continue;
      }

      var nextAncestors = Object.create(ancestors);
      nextAncestors[node.id] = true;

      // Every value here comes from the server, so every one goes through .text().
      var $row = $("<div>")
        .addClass("tree-node")
        .append($("<span>").addClass("tree-name").text(node.name))
        .append($("<span>").addClass("tree-meta").text("ID " + node.id + " · 排序 " + node.sortOrder));

      var $li = $("<li>").addClass("tree-depth-" + depth).append($row);

      var children = childrenOf[node.id];

      if (children && children.length) {
        $li.append(treeList(children, childrenOf, nextAncestors, depth + 1));
      }

      $list.append($li);
    }

    return $list;
  }

  /* ======================================================================
     Views
     ====================================================================== */

  function renderHome() {
    var $view = $("#view-home").empty();

    var $hero = $("<div>").addClass("card hero");

    $hero.append($("<h1>").text("校园二手交易平台"));
    $hero.append(
      $("<p>")
        .addClass("lede")
        .text(
          "北方工业大学校园内的二手物品交易平台。当前版本是前端地基：目录结构、统一的请求与错误" +
            "处理、分页、主题切换都已就位，并已用两个真实的字典接口跑通整条链路。"
        )
    );

    $view.append($hero);

    // Stated plainly rather than left for the user to discover by hunting for a product list that
    // does not exist. The API has no product endpoints yet, so there is nothing to link to.
    $view.append(
      notice("商品功能尚未开放：后端目前只有分类与宿舍区两个字典接口，商品接口将在下一阶段加入。")
    );

    var $entries = $("<div>").addClass("grid-2");

    $entries.append(
      $("<a>").attr("href", "#/categories").addClass("card entry-card")
        .append($("<strong>").text("分类字典"))
        .append($("<span>").text("GET /api/categories —— 扁平列表在客户端重建成树"))
    );

    $entries.append(
      $("<a>").attr("href", "#/dormitory-areas").addClass("card entry-card")
        .append($("<strong>").text("宿舍区字典"))
        .append($("<span>").text("GET /api/dormitory-areas —— 分页表格与按 ID 查询"))
    );

    $view.append($entries);

    var $status = $("<div>").addClass("card");
    $status.append($("<h2>").text("服务状态"));
    $status.append($("<div>").attr("data-region", "health").append(loading()));
    $view.append($status);

    loadHealth();
  }

  /**
   * Probes /health.
   *
   * Worth its own request because it is the only endpoint that is neither served by MVC nor shaped
   * as a problem document — a plain `{ "status": "ok" }` from a minimal-API MapGet. It is the one
   * place that proves the request layer copes with a response that is not part of the error
   * contract.
   */
  function loadHealth() {
    var $region = $("[data-region='health']").empty().append(loading());

    NcutApi.request("/health").then(
      function (body) {
        var ok = body && body.status === "ok";

        $region.empty().append(
          $("<div>").addClass("status-row")
            .append($("<span>").addClass("badge").addClass(ok ? "badge-ok" : "badge-bad")
              .text(ok ? "正常" : "异常"))
            .append($("<span>").addClass("muted").text("GET /health → status = " + (body && body.status)))
        );
      },
      function (error) {
        $region.empty().append(
          $("<div>").addClass("status-row")
            .append($("<span>").addClass("badge badge-bad").text("不可达"))
            .append($("<span>").addClass("muted").text(error.message))
        );
      }
    );
  }

  /**
   * Categories.
   *
   * The API returns a flat *paged* list; the tree is rebuilt here. Paging and a tree are
   * fundamentally at odds — a tree assembled from one page's worth of a subset is a tree with holes
   * in it, and nothing about the rendering would reveal that. So the whole dictionary is requested
   * in a single page at MaxPageSize, and when even that is not enough the page says so out loud
   * instead of drawing a partial tree that looks complete.
   *
   * The real fix is an unpaginated /api/categories/tree on the server — which the controller's own
   * remarks already anticipate, along with the warning that walking the tree lazily would be an
   * N+1. Until that exists, this banner is what keeps the limitation visible.
   */
  function renderCategories(force) {
    var $view = $("#view-categories").empty();

    var $card = $("<div>").addClass("card");

    var $head = $("<div>").addClass("card-head");
    $head.append(
      $("<div>")
        .append($("<h2>").text("分类字典"))
        .append($("<p>").addClass("muted").text("来源：GET /api/categories，客户端按 parentId 重建为树。"))
    );
    $head.append(
      $("<button>").attr({ type: "button", "data-action": "reload-categories" }).addClass("button").text("刷新")
    );
    $card.append($head);

    var $body = $("<div>").attr("data-region", "categories-body");
    $card.append($body);
    $view.append($card);

    if (state.categories && !force) {
      paintCategories($body, state.categories);
      return;
    }

    $body.append(loading());

    NcutApi.request("/api/categories", { query: { page: 1, pageSize: CATEGORY_PAGE_SIZE } }).then(
      function (page) {
        state.categories = page;
        // The view may have been navigated away from while this was in flight. Painting regardless
        // would write into a container that is no longer shown, and the newest request would then
        // be the one nobody sees.
        if (currentRoute() === "/categories") {
          paintCategories($body, page);
        }
      },
      function (error) {
        if (currentRoute() === "/categories") {
          $body.empty().append(errorCard(error, "reload-categories"));
        }
      }
    );
  }

  function paintCategories($body, page) {
    $body.empty();

    var items = page.items || [];

    if (page.totalCount > CATEGORY_PAGE_SIZE) {
      $body.append(
        notice(
          "共 " + page.totalCount + " 个分类，当前仅加载了前 " + CATEGORY_PAGE_SIZE +
            " 个，下方树形结构并不完整。分类字典接口尚未提供不分页的树形端点。"
        )
      );
    }

    if (items.length === 0) {
      $body.append(emptyState("没有可显示的分类。"));
      return;
    }

    var tree = buildTree(items);

    $body.append(
      $("<p>").addClass("muted").text("已加载 " + items.length + " 个分类，其中 " + tree.roots.length + " 个根节点。")
    );
    $body.append(treeList(tree.roots, tree.childrenOf, Object.create(null), 0));
  }

  function renderDormitoryAreas(force) {
    var $view = $("#view-dormitory-areas").empty();

    var $card = $("<div>").addClass("card");

    var $head = $("<div>").addClass("card-head");
    $head.append(
      $("<div>")
        .append($("<h2>").text("宿舍区字典"))
        .append($("<p>").addClass("muted").text("来源：GET /api/dormitory-areas"))
    );
    $head.append(
      $("<button>").attr({ type: "button", "data-action": "reload-dormitory" }).addClass("button").text("刷新")
    );
    $card.append($head);

    // The lookup box is not decoration. /api/dormitory-areas/{id} is the only endpoint in the API
    // that can answer 404, so it is the only way to reach the problem+json rendering path from the
    // UI at all. Without it, errorCard and the whole error-extraction branch in api.js would ship
    // having never once been executed.
    var $field = $("<div>").addClass("field");
    $field.append($("<label>").attr("for", "area-id").text("按 ID 查询"));
    $field.append(
      $("<input>").attr({ type: "number", id: "area-id", min: "1", placeholder: "例如 999999", inputmode: "numeric" })
    );
    $field.append(
      $("<button>").attr({ type: "button", "data-action": "lookup-area" }).addClass("button button-primary").text("查询")
    );
    $card.append($field);

    $card.append($("<div>").attr("data-region", "area-lookup"));

    var $body = $("<div>").attr("data-region", "dormitory-body");
    var $pagerRegion = $("<div>").attr("data-region", "dormitory-pager");

    $card.append($body);
    $card.append($pagerRegion);
    $view.append($card);

    if (state.lookup) {
      paintLookup($("[data-region='area-lookup']"), state.lookup);
    }

    if (state.dormitory.result && !force) {
      paintDormitory($body, $pagerRegion, state.dormitory.result);
      return;
    }

    $body.append(loading());
    loadDormitoryPage(state.dormitory.page);
  }

  function loadDormitoryPage(page) {
    var $body = $("[data-region='dormitory-body']");
    var $pagerRegion = $("[data-region='dormitory-pager']");

    state.dormitory.page = page;

    $pagerRegion.empty();
    $body.empty().append(loading());

    NcutApi.request("/api/dormitory-areas", {
      query: { page: page, pageSize: state.dormitory.pageSize }
    }).then(
      function (result) {
        state.dormitory.result = result;

        if (currentRoute() === "/dormitory-areas") {
          paintDormitory($body, $pagerRegion, result);
        }
      },
      function (error) {
        if (currentRoute() === "/dormitory-areas") {
          state.dormitory.result = null;
          $body.empty().append(errorCard(error, "reload-dormitory"));
        }
      }
    );
  }

  function paintDormitory($body, $pagerRegion, result) {
    $body.empty();
    $pagerRegion.empty();

    var items = result.items || [];

    if (items.length === 0) {
      $body.append(emptyState("这一页没有数据。"));
      $pagerRegion.append(pager(result.page, result.totalPages, result.totalCount));
      return;
    }

    var $table = $("<table>");

    var $headRow = $("<tr>");
    $headRow.append($("<th>").text("ID"));
    $headRow.append($("<th>").text("名称"));
    $headRow.append($("<th class='num'>").text("排序"));
    $headRow.append($("<th>").text("创建时间"));
    $headRow.append($("<th>").text("更新时间"));
    $table.append($("<thead>").append($headRow));

    var $tbody = $("<tbody>");

    for (var i = 0; i < items.length; i++) {
      var area = items[i];
      var $row = $("<tr>");

      // id and sortOrder are numbers straight off the wire; they still go through cell(), which is
      // the point of having one constructor — no call site gets to decide for itself that this
      // particular value is "safe enough" to concatenate.
      $row.append(cell(String(area.id)));
      $row.append(cell(area.name));
      $row.append(cell(String(area.sortOrder)).addClass("num"));
      $row.append(cell(formatDateTime(area.createdAt)));
      $row.append(cell(formatDateTime(area.updatedAt)));

      $tbody.append($row);
    }

    $table.append($tbody);
    $body.append($("<div>").addClass("table-scroll").append($table));
    $pagerRegion.append(pager(result.page, result.totalPages, result.totalCount));
  }

  function paintLookup($region, entry) {
    $region.empty();

    if (entry.error) {
      $region.append($("<div>").addClass("region-spaced").append(errorCard(entry.error, null)));
      return;
    }

    var area = entry.area;

    $region.append(
      $("<div>").addClass("card region-spaced")
        .append($("<h3>").text("查询结果"))
        .append(
          $("<div>").addClass("status-row")
            .append($("<span>").addClass("badge badge-ok").text("ID " + area.id))
            .append($("<span>").text(area.name))
            .append($("<span>").addClass("muted").text("排序 " + area.sortOrder))
            .append($("<span>").addClass("muted").text("创建于 " + formatDateTime(area.createdAt)))
        )
    );
  }

  function lookupArea() {
    var raw = $("#area-id").val();
    var id = parseInt(raw, 10);

    if (!isFinite(id) || id < 1) {
      toast("请输入一个正整数 ID。", "error");
      return;
    }

    var $region = $("[data-region='area-lookup']");
    $region.empty().append(loading());

    NcutApi.request("/api/dormitory-areas/" + id).then(
      function (area) {
        state.lookup = { area: area };
        paintLookup($region, state.lookup);
      },
      function (error) {
        state.lookup = { error: error };
        paintLookup($region, state.lookup);

        // The card already shows the message; the toast is what confirms the button press was seen
        // when the result lands below the fold.
        toast(error.status === 404 ? "没有找到 ID 为 " + id + " 的宿舍区。" : error.message, "error");
      }
    );
  }

  /* ======================================================================
     Routing
     ====================================================================== */

  // Route table. Each entry pairs the view container with the renderer, so navigation is a lookup
  // rather than a chain of string comparisons that has to be kept in step with the handlers by hand.
  var routes = {
    "/": { view: "view-home", render: renderHome },
    "/categories": { view: "view-categories", render: renderCategories },
    "/dormitory-areas": { view: "view-dormitory-areas", render: renderDormitoryAreas }
  };

  function currentRoute() {
    var path = (window.location.hash || "").replace(/^#/, "");

    // A hash carrying a query string or a trailing slash still has to land on a view rather than on
    // the not-found page. The fallback covers both "" (no hash at all) and "/" collapsing to "".
    return path.split("?")[0].replace(/\/+$/, "") || "/";
  }

  function showView(id) {
    $("#view-home, #view-categories, #view-dormitory-areas, #view-missing").attr("hidden", true);
    $("#" + id).removeAttr("hidden");
  }

  function navigate() {
    var route = currentRoute();
    var match = routes[route];

    // aria-current is what a screen reader announces; .is-active is only the styling hook. Keeping
    // them separate means the visual treatment can change without silently changing the semantics.
    $("#site-nav a").removeAttr("aria-current").removeClass("is-active");
    $("#site-nav a[href='#" + route + "']").attr("aria-current", "page").addClass("is-active");

    if (!match) {
      $("#missing-hash").text(window.location.hash || "(空)");
      showView("view-missing");
      return;
    }

    showView(match.view);
    match.render(false);
  }

  /* ======================================================================
     Theme
     ====================================================================== */

  function effectiveTheme() {
    var explicit = document.documentElement.getAttribute("data-theme");

    if (explicit === "dark" || explicit === "light") {
      return explicit;
    }

    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
      ? "dark"
      : "light";
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);

    try {
      localStorage.setItem(THEME_KEY, theme);
    } catch (e) {
      // Storage can be unavailable; the theme still applies for this page view.
    }

    paintThemeButton(theme);
  }

  function paintThemeButton(theme) {
    // The icon shows the theme currently in effect, and the title says what a press will do. Both
    // are set from the same call so they cannot disagree after a toggle.
    //
    // The <use> element's href is written with setAttribute rather than jQuery's .attr(): this is an
    // SVG element, and going through the DOM directly leaves no doubt about which attribute is
    // being written.
    var icon = document.getElementById("theme-icon");

    if (icon) {
      icon.setAttribute("href", theme === "dark" ? "#i-moon" : "#i-sun");
    }

    $("#theme-toggle").attr(
      "title",
      "切换到" + (theme === "dark" ? "浅色" : "深色") + "主题"
    );
  }

  /* ======================================================================
     Events
     ====================================================================== */

  // Delegated, and bound exactly once. The sibling project re-binds after every render because it
  // replaces innerHTML wholesale; with delegation that is unnecessary, and it also removes a whole
  // class of bug where a control works on first visit and does nothing on the second because the
  // re-render dropped its listener.
  $(document)
    .on("click", "[data-action='reload-categories']", function () {
      state.categories = null;
      renderCategories(true);
    })
    .on("click", "[data-action='reload-dormitory']", function () {
      state.dormitory.result = null;
      renderDormitoryAreas(true);
    })
    .on("click", "[data-retry]", function () {
      var action = $(this).attr("data-retry");

      if (action === "reload-categories") {
        state.categories = null;
        renderCategories(true);
      } else if (action === "reload-dormitory") {
        state.dormitory.result = null;
        renderDormitoryAreas(true);
      }
    })
    .on("click", "[data-action='page']", function () {
      var target = parseInt($(this).attr("data-page"), 10);

      if (isFinite(target) && target >= 1) {
        loadDormitoryPage(target);
      }
    })
    .on("click", "[data-action='lookup-area']", lookupArea)
    .on("keydown", "#area-id", function (event) {
      if (event.key === "Enter") {
        event.preventDefault();
        lookupArea();
      }
    })
    .on("click", "#theme-toggle", function () {
      applyTheme(effectiveTheme() === "dark" ? "light" : "dark");
    });

  $(window).on("hashchange", navigate);

  /* ======================================================================
     Boot
     ====================================================================== */

  $(function () {
    paintThemeButton(effectiveTheme());
    navigate();
  });
})(jQuery, window.NcutApi);

/* 三个页面：首页、分类字典、宿舍区字典。
 * 视图整块用 .html() 重建，事件一律委托在 document 上绑一次。 */
(function ($) {
  "use strict";

  // 服务端来的字符串插进 HTML 前先转义。
  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  // 接口发的是北京时间的墙上时钟字符串（"2026-09-29T13:43:09.037"，无时区后缀）。
  // 直接按文本重排，不构造 Date。
  function formatDateTime(value) {
    return value ? String(value).replace("T", " ").slice(0, 16) : "—";
  }

  function loading() {
    return '<div class="loading">加载中…</div>';
  }

  function errorCard(error, retry) {
    return '<div class="error-card"><h3>加载失败</h3>' +
      '<p>' + esc(error.message) + '</p>' +
      (error.code ? '<div class="error-meta">code: <code>' + esc(error.code) + '</code>' +
        (error.traceId ? ' · traceId: <code>' + esc(error.traceId) + '</code>' : '') + '</div>' : '') +
      (retry ? '<button class="button" data-reload="' + retry + '">重试</button>' : '') +
      '</div>';
  }

  /* ---------- 首页 ---------- */

  function showHome() {
    $("#view").html(
      '<div class="card">' +
      '<h1>校园二手交易平台</h1>' +
      '<p class="lede">北方工业大学校园内的二手物品交易平台。后端目前只有分类与宿舍区两个字典' +
      '接口，商品功能还没做，所以这一版只有下面两个页面。</p>' +
      '</div>' +
      '<div class="grid-2">' +
      '<a class="card entry-card" href="#/categories">' +
      '<strong>分类字典</strong><span>GET /api/categories</span></a>' +
      '<a class="card entry-card" href="#/dormitory-areas">' +
      '<strong>宿舍区字典</strong><span>GET /api/dormitory-areas</span></a>' +
      '</div>' +
      '<div class="card"><h2>服务状态</h2><div id="health">' + loading() + '</div></div>');

    api("/health").then(function (body) {
      $("#health").html('<span class="badge badge-ok">正常</span> status = ' + esc(body.status));
    }, function (error) {
      $("#health").html('<span class="badge badge-bad">不可达</span> ' + esc(error.message));
    });
  }

  /* ---------- 分类字典 ---------- */

  // 扁平列表按 parentId 递归成嵌套的 <ul>。接口最多给 100 条，够用。
  function treeHtml(items, parentId, depth) {
    var children = items.filter(function (item) {
      return item.parentId === parentId;
    });

    if (!children.length) {
      return "";
    }

    return '<ul class="' + (depth ? "tree-child" : "tree") + '">' +
      children.map(function (item) {
        return '<li>' +
          '<div class="tree-node">' +
          '<span class="tree-name">' + esc(item.name) + '</span>' +
          '<span class="tree-meta">ID ' + item.id + ' · 排序 ' + item.sortOrder + '</span>' +
          '</div>' +
          treeHtml(items, item.id, depth + 1) +
          '</li>';
      }).join("") +
      '</ul>';
  }

  function showCategories() {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>分类字典</h2>' +
      '<button class="button" data-reload="categories">刷新</button></div>' +
      '<div id="tree">' + loading() + '</div>' +
      '</div>');

    api("/api/categories", { page: 1, pageSize: 100 }).then(function (page) {
      var html = treeHtml(page.items, null, 0);

      $("#tree").html((html ? html : '<div class="empty">没有分类。</div>') +
        '<p class="muted">共 ' + page.totalCount + ' 个分类。</p>');
    }, function (error) {
      $("#tree").html(errorCard(error, "categories"));
    });
  }

  /* ---------- 宿舍区字典 ---------- */

  function pagerHtml(result) {
    return '<div class="pager">' +
      '<span class="pager-status">共 ' + result.totalCount + ' 条 · 第 ' +
      result.page + '/' + result.totalPages + ' 页</span>' +
      '<div class="pager-actions">' +
      '<button class="button" data-page="' + (result.page - 1) + '"' +
      (result.page <= 1 ? " disabled" : "") + '>上一页</button>' +
      '<button class="button" data-page="' + (result.page + 1) + '"' +
      (result.page >= result.totalPages ? " disabled" : "") + '>下一页</button>' +
      '</div></div>';
  }

  function showDormitoryAreas(page) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>宿舍区字典</h2>' +
      '<button class="button" data-reload="dormitory">刷新</button></div>' +
      '<div class="field">' +
      '<label for="area-id">按 ID 查询</label>' +
      '<input type="number" id="area-id" min="1" placeholder="例如 999999">' +
      '<button class="button" data-action="lookup">查询</button>' +
      '</div>' +
      '<div id="lookup"></div>' +
      '<div id="list">' + loading() + '</div>' +
      '</div>');

    loadPage(page || 1);
  }

  function loadPage(page) {
    $("#list").html(loading());

    api("/api/dormitory-areas", { page: page, pageSize: 20 }).then(function (result) {
      if (!result.items.length) {
        $("#list").html('<div class="empty">这一页没有数据。</div>' + pagerHtml(result));
        return;
      }

      $("#list").html(
        '<div class="table-scroll"><table><thead><tr>' +
        '<th>ID</th><th>名称</th><th class="num">排序</th><th>创建时间</th><th>更新时间</th>' +
        '</tr></thead><tbody>' +
        result.items.map(function (area) {
          return '<tr>' +
            '<td>' + area.id + '</td>' +
            '<td>' + esc(area.name) + '</td>' +
            '<td class="num">' + area.sortOrder + '</td>' +
            '<td>' + formatDateTime(area.createdAt) + '</td>' +
            '<td>' + formatDateTime(area.updatedAt) + '</td>' +
            '</tr>';
        }).join("") +
        '</tbody></table></div>' + pagerHtml(result));
    }, function (error) {
      $("#list").html(errorCard(error, "dormitory"));
    });
  }

  // /api/dormitory-areas/{id} 是唯一会返回 404 的接口，用它验证错误卡这条路。
  function lookupArea() {
    var id = parseInt($("#area-id").val(), 10);

    if (!id) {
      $("#lookup").html('<div class="error-card"><p>请输入一个数字 ID。</p></div>');
      return;
    }

    $("#lookup").html(loading());

    api("/api/dormitory-areas/" + id).then(function (area) {
      $("#lookup").html(
        '<div class="card"><h3>' + esc(area.name) + '</h3>' +
        '<p class="muted">ID ' + area.id + ' · 排序 ' + area.sortOrder +
        ' · 创建于 ' + formatDateTime(area.createdAt) + '</p></div>');
    }, function (error) {
      $("#lookup").html(errorCard(error, null));
    });
  }

  /* ---------- 路由 ---------- */

  function route() {
    var path = (location.hash || "").replace(/^#/, "");

    if (path === "/categories") {
      showCategories();
    } else if (path === "/dormitory-areas") {
      showDormitoryAreas(1);
    } else if (path === "/" || path === "") {
      path = "/";
      showHome();
    } else {
      $("#view").html('<div class="card state-card"><h1>页面不存在</h1>' +
        '<p class="muted">' + esc(location.hash) + '</p>' +
        '<a class="button" href="#/">回到首页</a></div>');
    }

    $("#site-nav a").removeClass("is-active").removeAttr("aria-current");
    $("#site-nav a[href='#" + path + "']").addClass("is-active").attr("aria-current", "page");
  }

  /* ---------- 主题 ---------- */

  function currentTheme() {
    var set = document.documentElement.getAttribute("data-theme");
    return set || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  }

  function paintThemeButton() {
    $("#theme-toggle").text(currentTheme() === "dark" ? "浅色" : "深色");
  }

  function toggleTheme() {
    var next = currentTheme() === "dark" ? "light" : "dark";

    document.documentElement.setAttribute("data-theme", next);

    try {
      localStorage.setItem("theme", next);
    } catch (e) { }

    paintThemeButton();
  }

  /* ---------- 事件：委托在 document 上，绑一次 ---------- */

  $(document)
    .on("click", "[data-reload]", function () {
      if ($(this).attr("data-reload") === "categories") {
        showCategories();
      } else {
        showDormitoryAreas(1);
      }
    })
    .on("click", "[data-page]", function () {
      loadPage(parseInt($(this).attr("data-page"), 10));
    })
    .on("click", "[data-action='lookup']", lookupArea)
    .on("keydown", "#area-id", function (event) {
      if (event.key === "Enter") {
        lookupArea();
      }
    })
    .on("click", "#theme-toggle", toggleTheme);

  $(window).on("hashchange", route);

  $(function () {
    paintThemeButton();
    route();
  });
})(jQuery);

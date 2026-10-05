/* 路由与全局事件。
 *
 * 视图整块用 .html() 重建，事件一律委托在 document 上绑一次——
 * 这样页面换掉之后不用重新绑，也不会有监听器泄漏。 */
(function ($) {
  "use strict";

  var NM = window.NM;

  /* 路由表。顺序有意义：/products/new 必须排在 /products/(\d+) 前面，
   * 否则以后加了更宽的规则会先被吃掉。 */
  var routes = [
    [/^\/$/, function () { showHome(); }],
    [/^\/products$/, function (match, query) { products.showList(query); }],
    [/^\/products\/new$/, function () { products.showCreate(); }],
    [/^\/products\/(\d+)\/edit$/, function (match) { products.showEdit(match[1]); }],
    [/^\/products\/(\d+)$/, function (match) { products.showDetail(match[1]); }],
    [/^\/mine$/, function (match, query) { products.showMine(query); }],
    [/^\/messages$/, function (match, query) { messages.showList(query); }],
    [/^\/messages\/(\d+)$/, function (match, query) { messages.showThread(match[1], query); }],
    [/^\/announcements$/, function (match, query) { announcements.showList(query); }],
    [/^\/login$/, function () { auth.showLogin(); }],
    [/^\/register$/, function () { auth.showRegister(); }],
    // query 要传下去：宿舍区那页是分页的，不传的话翻到第 2 页再点刷新就跳回第 1 页。
    [/^\/categories$/, function () { dictionaries.showCategories(); }],
    [/^\/dormitory-areas$/, function (match, query) { dictionaries.showDormitoryAreas(query); }]
  ];

  /* hash 拆成路径和查询串两半。筛选条件放在查询串里，所以刷新和后退都能回到同一页。 */
  function parseHash() {
    var raw = (location.hash || "").replace(/^#/, "");
    var mark = raw.indexOf("?");
    var path = mark === -1 ? raw : raw.slice(0, mark);
    var query = {};

    if (mark !== -1) {
      raw.slice(mark + 1).split("&").forEach(function (pair) {
        if (!pair) {
          return;
        }

        var eq = pair.indexOf("=");
        var key = eq === -1 ? pair : pair.slice(0, eq);
        var value = eq === -1 ? "" : pair.slice(eq + 1);

        try {
          query[decodeURIComponent(key)] = decodeURIComponent(value.replace(/\+/g, " "));
        } catch (e) {
          // 坏掉的百分号编码不该让整页白屏，跳过这一段就是。
        }
      });
    }

    return { path: path === "" ? "/" : path, query: query };
  }

  function route() {
    var parsed = parseHash();

    for (var index = 0; index < routes.length; index++) {
      var match = routes[index][0].exec(parsed.path);

      if (match) {
        routes[index][1](match, parsed.query);
        paintNav(parsed.path);

        /* 首页那两张入口卡是渲染时才有的，所以管理入口不能只在登录状态变化时刷一次，
         * 每次换页都要跟着重刷。 */
        paintAdminNav();
        return;
      }
    }

    $("#view").html('<div class="card state-card"><h1>页面不存在</h1>' +
      '<p class="muted">' + NM.esc(location.hash) + '</p>' +
      '<a class="button" href="#/products">去逛商品</a></div>');

    paintNav(null);
  }

  function paintNav(path) {
    $("#site-nav a").removeClass("is-active").removeAttr("aria-current");
    $("#site-nav a[href='#" + path + "']").addClass("is-active").attr("aria-current", "page");
  }

  /* 分类和宿舍区的管理入口只给管理员看。藏的是导航两条链接和首页那两张入口卡，
   * 不是访问控制——路由守卫在 dictionaries.js 里，接口那边的判定在服务端。 */
  function paintAdminNav() {
    var current = auth.user();
    var admin = !!current && current.role === 2;

    $("#nav-categories").toggle(admin);
    $("#nav-dormitory-areas").toggle(admin);
    $("#admin-entries").toggle(admin);
  }

  /* ---------- 首页 ---------- */

  function showHome() {
    $("#view").html(
      '<div class="card">' +
      '<h1>校园二手交易平台</h1>' +
      '<p class="lede">北方工业大学校园内的二手物品交易平台。把闲置的东西挂上来，' +
      '或者看看别人在卖什么。</p>' +
      '<div class="action-bar">' +
      '<a class="button button-primary" href="#/products">去逛商品</a>' +
      '<a class="button" href="#/products/new">发布商品</a>' +
      '</div></div>' +
      '<div class="grid-2" id="admin-entries">' +
      '<a class="card entry-card" href="#/categories">' +
      '<strong>分类字典</strong><span>GET /api/categories</span></a>' +
      '<a class="card entry-card" href="#/dormitory-areas">' +
      '<strong>宿舍区字典</strong><span>GET /api/dormitory-areas</span></a>' +
      '</div>');
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

  /* ---------- 页脚计数器 ---------- */

  /* 千禧年门户的标配。纯装饰：数字存在 localStorage 里，每加载一次加一，
   * 不接任何真实统计——刷新几遍就会发现自己「访问」了好多次。
   * 拿不到 localStorage 就渲染 000000，不报错也不留空白。 */
  function paintVisitCounter() {
    var slot = $("#visit-counter");

    if (!slot.length) {
      return;
    }

    var count = 0;

    try {
      count = parseInt(localStorage.getItem("ncut.visits"), 10) || 0;
    } catch (e) {
      // 无痕模式下 localStorage 可能直接抛异常。计数是装饰，不必为此中断启动。
    }

    count++;

    try {
      localStorage.setItem("ncut.visits", String(count));
    } catch (e) { }

    var text = String(count);

    while (text.length < 6) {
      text = "0" + text;
    }

    // 一格一位。字符只可能是 0-9，所以这里不需要 esc()。
    slot.html(text.replace(/./g, "<span>$&</span>"));
  }

  /* ---------- 事件：委托在 document 上，绑一次 ---------- */

  $(document)
    // 重试/刷新统一走一次路由：重新执行当前页面就是重新拉一次数据。
    .on("click", "[data-reload]", route)
    .on("click", "#theme-toggle", toggleTheme)
    .on("submit", "#search-form", function (event) {
      event.preventDefault();
      products.submitSearch();
    })
    .on("submit", "#create-form", function (event) {
      event.preventDefault();
      products.submitCreate();
    })
    .on("submit", "#edit-form", function (event) {
      event.preventDefault();
      products.submitEdit(matchProductId());
    })
    .on("submit", "#login-form", function (event) {
      event.preventDefault();
      auth.submitLogin();
    })
    .on("submit", "#register-form", function (event) {
      event.preventDefault();
      auth.submitRegister();
    })
    .on("click", "[data-action='sign-out']", function () {
      auth.signOut();
      location.hash = "#/products";
    })
    .on("click", "[data-action='publish']", function () { products.transition("publish"); })
    .on("click", "[data-action='offline']", function () { products.transition("offline"); })
    .on("click", "[data-action='sold']", function () { products.transition("sold"); })
    .on("click", "[data-action='delete']", function () { products.deleteProduct(); })
    .on("click", "[data-action='upload-image']", function () { products.uploadImage(); })
    .on("click", "[data-action='delete-image']", function () {
      products.deleteImage($(this).attr("data-image-id"));
    })
    .on("click", "[data-action='contact-seller']", function () {
      messages.start($(this).attr("data-product-id"));
    })
    .on("submit", "#message-form", function (event) {
      event.preventDefault();
      messages.send();
    })
    .on("submit", "#announcement-form", function (event) {
      event.preventDefault();
      announcements.submitPublish();
    })
    .on("click", "[data-action='announcement-dismiss']", function () {
      announcements.dismiss($(this).attr("data-announcement-id"));
    })
    .on("click", "[data-action='delete-announcement']", function () {
      announcements.deleteAnnouncement($(this).attr("data-announcement-id"));
    })
    .on("submit", "#category-form", function (event) {
      event.preventDefault();
      dictionaries.submitCategory();
    })
    .on("submit", "#area-form", function (event) {
      event.preventDefault();
      dictionaries.submitArea();
    })
    .on("click", "[data-action='edit-category']", function () {
      dictionaries.editCategory($(this).attr("data-category-id"));
    })
    .on("click", "[data-action='cancel-category']", function () {
      dictionaries.cancelCategory();
    })
    .on("click", "[data-action='delete-category']", function () {
      dictionaries.deleteCategory($(this).attr("data-category-id"));
    })
    .on("click", "[data-action='edit-area']", function () {
      dictionaries.editArea($(this).attr("data-area-id"));
    })
    .on("click", "[data-action='cancel-area']", function () {
      dictionaries.cancelArea();
    })
    .on("click", "[data-action='delete-area']", function () {
      dictionaries.deleteArea($(this).attr("data-area-id"));
    });

  /* 编辑表单提交时要知道改的是哪个 id。事件是委托的，处理函数不在渲染时的闭包里，
   * 所以从 hash 现读。 */
  function matchProductId() {
    var match = /^#\/products\/(\d+)/.exec(location.hash);

    return match ? match[1] : null;
  }

  $(window).on("hashchange", route);

  $(function () {
    paintThemeButton();
    paintVisitCounter();

    /* 未读徽标和管理入口都跟着登录状态走。注册这两个回调就够：下面的 auth.refresh() 会走
     * paint()，之后的登录、退出也都走同一处。
     *
     * 注意 paint() 是 callback() 无参调的，所以 paintAdminNav 自己读 auth.user()，
     * 不能指望参数里有当前用户。 */
    auth.onUserChanged(messages.refreshUnread);
    auth.onUserChanged(paintAdminNav);

    announcements.refreshBar();

    // 本地缓存的用户信息可能过期（比如 token 还在但账号被停用了），
    // 首屏先按缓存渲染，然后向服务端核一次。
    auth.refresh();

    route();
  });
})(jQuery);

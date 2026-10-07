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
    [/^\/notifications$/, function (match, query) { notifications.showList(query); }],
    [/^\/announcements$/, function (match, query) { announcements.showList(query); }],
    [/^\/login$/, function () { auth.showLogin(); }],
    [/^\/register$/, function () { auth.showRegister(); }],
    [/^\/forgot$/, function () { auth.showForgot(); }],
    // query 要传下去：宿舍区那页是分页的，不传的话翻到第 2 页再点刷新就跳回第 1 页。
    [/^\/categories$/, function () { dictionaries.showCategories(); }],
    [/^\/dormitory-areas$/, function (match, query) { dictionaries.showDormitoryAreas(query); }],
    // 关键字和页码同理，都在 query 里。
    [/^\/users$/, function (match, query) { users.showUsers(query); }]
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

        /* 首页的入口卡和商品列表的发布按钮都是渲染时才有的，所以这些入口不能只在登录
         * 状态变化时刷一次，每次换页都要跟着重刷。 */
        paintNavVisibility();
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

  /* 导航里哪些入口给人看。两条规则——发布要登录、管理入口要管理员——写在一个函数里，因为
   * 触发点完全一样（每次换页、每次登录状态变化），拆开就是同一件事读两遍 auth.user()。
   *
   * 藏起来不是访问控制：路由守卫在 dictionaries.js / users.js 里，接口那边的判定在服务端。
   *
   * 只有发布是藏掉的。「个人主页」「私信」「通知」不藏——未登录点进去看到的是 NM.signInCard()
   * 那张「请先登录」，比一个凭空消失的入口好解释，也顺带告诉人登录了能干什么。 */
  function paintNavVisibility() {
    var current = auth.user();
    var admin = !!current && current.role === 2;

    /* 发布入口有三处：顶栏、首页、商品列表的卡片头。用属性选择器而不是逐个 id——
     * 以后再加一处，带上 data-signin-only 就自动跟着走。 */
    $("[data-signin-only]").toggle(auth.isSignedIn());

    $("#nav-categories").toggle(admin);
    $("#nav-dormitory-areas").toggle(admin);
    $("#nav-users").toggle(admin);
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
      '<a class="button" href="#/products/new" data-signin-only>发布商品</a>' +
      '</div></div>' +
      '<div class="grid-2" id="admin-entries">' +
      '<a class="card entry-card" href="#/categories">' +
      '<strong>分类字典</strong><span>GET /api/categories</span></a>' +
      '<a class="card entry-card" href="#/dormitory-areas">' +
      '<strong>宿舍区字典</strong><span>GET /api/dormitory-areas</span></a>' +
      '<a class="card entry-card" href="#/users">' +
      '<strong>用户管理</strong><span>忘了密码的人来这里领重置码</span></a>' +
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

  /* ---------- 页脚的在线人数 ---------- */

  /* 六位数字，一位一格——千禧年门户计数器那个长相，等宽由 CSS 的 .online-count span 给。
   * 字符只可能是 0-9，所以这里不需要 esc()。
   *
   * 兜底成 0 而不是留着原样：接口要是挂了、字段改了名、或者回了个 {}，data.onlineCount 就是
   * undefined，直接补零会得到 "00undefined" ——页脚上会是几个莫名其妙的方格。 */
  function visitDigits(count) {
    var text = String(Math.max(0, Math.floor(Number(count) || 0)));

    while (text.length < 6) {
      text = "0" + text;
    }

    return text.replace(/./g, "<span>$&</span>");
  }

  /* 这个数是「最近几分钟内有请求的活跃账号」，不是「此刻开着页面的人」。JWT 没有会话，
   * 没登录的游客在服务端认不出来是谁，所以算不进去——页脚那句说明就是为这个写的。
   *
   * 不轮询：和未读徽标一样只在页面加载时拉一次，所以它是一个快照，坐着不动就不会变。
   * 拉不到就留空——宁可什么都没有，也不要显示一个编出来的 0。 */
  function paintOnlineCount() {
    var slot = $("#online-count");

    if (!slot.length) {
      return;
    }

    api.get("/api/online/count").then(function (data) {
      slot.html(visitDigits(data.onlineCount));
    }, function () { });
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
    .on("submit", "#forgot-form", function (event) {
      event.preventDefault();
      auth.submitForgot();
    })
    .on("click", "[data-action='sign-out']", function () {
      auth.signOut();
      location.hash = "#/products";
    })
    .on("click", "[data-action='publish']", function () { products.transition("publish"); })
    .on("click", "[data-action='offline']", function () { products.transition("offline"); })
    .on("click", "[data-action='sold']", function () { products.transition("sold"); })
    .on("click", "[data-action='delete']", function () {
      products.deleteProduct($(this).attr("data-keeps-trade-record"));
    })
    .on("click", "[data-action='upload-image']", function () { products.uploadImage(); })
    .on("click", "[data-action='delete-image']", function () {
      products.deleteImage($(this).attr("data-image-id"));
    })
    /* 看图浮层。删除按钮是图格的兄弟节点，不是子节点，所以点删除不会顺带弹浮层。 */
    .on("click", "[data-action='view-image']", function () {
      products.openImage($(this).attr("data-full"));
    })
    .on("click", "#lightbox", function () { products.closeImage(); })
    .on("keydown", function (event) {
      if (event.key === "Escape") {
        products.closeImage();
      }
    })
    .on("click", "#generate-cover", function () { products.generateCover(); })
    .on("click", "[data-action='discard-cover']", function () { products.discardCover(); })
    .on("click", "[data-action='contact-seller']", function () {
      messages.start($(this).attr("data-product-id"));
    })
    .on("submit", "#message-form", function (event) {
      event.preventDefault();
      messages.send();
    })
    /* 四个交易动作一个处理函数。按钮在 messages.js 里渲染，路径表也在那边，
     * 这里只负责把点击转发过去，并把通知徽标的刷新接上——交易动作会给双方都写通知，
     * 包括我自己。 */
    .on("click", "[data-action^='trade-']", function () {
      messages.tradeAction($(this).attr("data-action"), notifications.refreshUnread);
    })
    .on("click", "[data-action='open-notification']", function () {
      notifications.open($(this).attr("data-notification-id"));
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
    })
    .on("submit", "#user-search-form", function (event) {
      event.preventDefault();
      users.submitSearch();
    })
    .on("click", "[data-action='clear-user-search']", function () { users.clearSearch(); })
    .on("click", "[data-action='reset-password']", function () {
      users.issueResetCode($(this).attr("data-user-id"));
    })
    .on("click", "[data-action='copy-reset-code']", function () { users.copyResetCode(); })
    .on("click", "[data-action='dismiss-reset-code']", function () { users.dismissResetCode(); });

  /* 编辑表单提交时要知道改的是哪个 id。事件是委托的，处理函数不在渲染时的闭包里，
   * 所以从 hash 现读。 */
  function matchProductId() {
    var match = /^#\/products\/(\d+)/.exec(location.hash);

    return match ? match[1] : null;
  }

  $(window).on("hashchange", route);

  $(function () {
    paintThemeButton();
    paintOnlineCount();

    /* 未读徽标和管理入口都跟着登录状态走。注册这两个回调就够：下面的 auth.refresh() 会走
     * paint()，之后的登录、退出也都走同一处。
     *
     * 注意 paint() 是 callback() 无参调的，所以 paintNavVisibility 自己读 auth.user()，
     * 不能指望参数里有当前用户。 */
    auth.onUserChanged(messages.refreshUnread);
    auth.onUserChanged(notifications.refreshUnread);
    auth.onUserChanged(paintNavVisibility);

    announcements.refreshBar();

    // 本地缓存的用户信息可能过期（比如 token 还在但账号被停用了），
    // 首屏先按缓存渲染，然后向服务端核一次。
    auth.refresh();

    route();
  });
})(jQuery);

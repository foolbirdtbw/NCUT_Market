/* 小助手小北。右下角那个常驻浮窗。
 *
 * 两条来路的建议，刻意分得很开：
 *   - 提示（hints）是服务端按页算出来的，免费、即时，谁都能看；
 *   - 提问每问一句是一次模型调用，占当天额度，要登录。
 *
 * 她的容器在 index.html 里、#view 外面——路由每次都用 .html() 整块重建 #view，
 * 放进去活不过一次换页。所以这里只跟着路由换内容，不跟着路由重建自己。
 *
 * 对话历史只在这个闭包的内存里，不写 localStorage、不落库：刷新就没了，这是有意的。 */
window.agent = (function ($) {
  "use strict";

  var NM = window.NM;

  // 发给服务端的历史上限。服务端还会再截一次，这里是让发出去的包先小下来。
  var HISTORY_LIMIT = 8;
  var HISTORY_CONTENT_LIMIT = 300;

  var state = {
    open: false,
    page: "general",
    productId: null,
    history: [],
    busy: false
  };

  /* 当前路径对应的页名，和服务端 AgentAssistant 里的 slug 一一对应。
   * 认不出来的话给 "general"，服务端会拿通用文案兜底。 */
  function agentPageFor(path) {
    if (!path || path === "/") {
      return "home";
    }

    if (path === "/products") {
      return "products";
    }

    if (path === "/products/new") {
      return "create";
    }

    if (/^\/products\/\d+(\/edit)?$/.test(path)) {
      return "product";
    }

    if (path === "/mine") {
      return "mine";
    }

    if (/^\/messages(\/\d+)?$/.test(path)) {
      return "messages";
    }

    if (path === "/notifications") {
      return "notifications";
    }

    if (path === "/feedback") {
      return "feedback";
    }

    return "general";
  }

  /* 商品详情和编辑页都要带着 id；编辑页按详情页处理——它有同一件商品，但没有选照片那一栏。 */
  function agentProductId(path) {
    var match = /^\/products\/(\d+)(?:\/edit)?$/.exec(path || "");

    return match ? match[1] : null;
  }

  /* 只留最后 HISTORY_LIMIT 条，role 只认 user/assistant，单条截断，空的丢掉。
   * 服务端会再校验一遍——浏览器是唯一存历史的地方，也是唯一能把 prompt 撑大的地方。 */
  function trimHistory(history) {
    var kept = [];

    if (!history || !history.length) {
      return kept;
    }

    for (var index = 0; index < history.length; index++) {
      var turn = history[index];

      if (!turn || (turn.role !== "user" && turn.role !== "assistant")) {
        continue;
      }

      var content = typeof turn.content === "string" ? turn.content.trim() : "";

      if (!content) {
        continue;
      }

      kept.push({
        role: turn.role,
        content: content.length > HISTORY_CONTENT_LIMIT
          ? content.slice(0, HISTORY_CONTENT_LIMIT)
          : content
      });
    }

    return kept.slice(-HISTORY_LIMIT);
  }

  function panel() {
    return $("#agent-panel");
  }

  function face() {
    return $("#agent-face");
  }

  /* 发布助手那一块。整块只有发布页才有——它要往 #form-title / #form-description /
   * #form-price 里填字，那是那张表单自己的格子；离开这一页还挂着就是死按钮。 */
  function renderTools() {
    if (!$("#agent-tools").is(":empty") || !window.products) {
      return;
    }

    $("#agent-tools").html('<div class="field-stack">' +
      '<button type="button" class="button" id="ask-ai">帮我看照片写商品信息</button>' +
      '<span class="hint">选好照片再点，照片会离开本站发到 AI 服务，我们不留存。</span>' +
      window.products.aiKeyHtml() +
      '<div id="agent-listing-slot"></div>' +
      '</div>');
  }

  function onRoute(path) {
    var previous = state.page;

    state.page = agentPageFor(path);
    state.productId = agentProductId(path);

    if (previous === "create" && state.page !== "create") {
      $("#agent-tools").empty();
    }

    if (state.page === "create") {
      renderTools();
    }

    // 只上样式，不额外发请求：脸上那个点告诉人「这页有点东西可看」。
    face().toggleClass("has-tools", state.page === "create");

    paintComposer();

    // 关着就不拉提示。不然每翻一页都白查一次库，给一个没人打开的面板。
    if (state.open) {
      refreshHints();
    }
  }

  function open() {
    state.open = true;
    panel().prop("hidden", false);
    face().attr("aria-expanded", "true");

    paintComposer();
    refreshHints();

    $("#agent-input").trigger("focus");
  }

  function close() {
    state.open = false;
    panel().prop("hidden", true);
    face().attr("aria-expanded", "false");
  }

  function toggle() {
    if (state.open) {
      close();
    } else {
      open();
    }
  }

  function refreshHints() {
    var data = { page: state.page };

    if (state.productId) {
      data.productId = state.productId;
    }

    api.get("/api/ai/agent/hints", data).then(function (result) {
      renderHints(result.hints || []);
      paintQuota(result.remainingToday);
    }, function (error) {
      $("#agent-hints").html(NM.inlineError(error));
    });
  }

  function renderHints(hints) {
    var parts = [];

    hints.forEach(function (hint) {
      if (hint.href) {
        parts.push('<a class="agent-hint" href="' + NM.esc(hint.href) + '">' +
          NM.esc(hint.text) + '</a>');
      } else {
        parts.push('<p class="agent-hint">' + NM.esc(hint.text) + '</p>');
      }
    });

    $("#agent-hints").html(parts.join(""));
  }

  function paintQuota(remaining) {
    if (!auth.isSignedIn()) {
      $("#agent-quota").html('<a href="#/login">登录</a>之后可以接着问我。');
      return;
    }

    $("#agent-quota").text(remaining == null ? "" : "今天还剩 " + remaining + " 次");
  }

  /* 未登录时输入框是关的。不是靠这里拦——接口那边的 [Authorize] 才是判定——而是
   * 让人在打字之前就知道要先去登录。 */
  function paintComposer() {
    var signedIn = auth.isSignedIn();

    $("#agent-input").prop("disabled", !signedIn)
      .attr("placeholder", signedIn ? "问我点什么…" : "登录之后可以提问");
    $("#agent-send").prop("disabled", !signedIn || state.busy);
  }

  function appendBubble(role, text) {
    var log = $("#agent-log");

    log.append('<div class="agent-bubble is-' + role + '">' + NM.esc(text) + '</div>');
    log.scrollTop(log[0].scrollHeight);
  }

  /* 搜索命中的商品，接在回答气泡下面。链接是这里拼的：服务端只给 id 和标题，
   * 模型看不到 id，所以这一行永远指向一件真在卖的东西。 */
  function appendResults(results) {
    if (!results || !results.length) {
      return;
    }

    var log = $("#agent-log");
    var parts = [];

    results.forEach(function (item) {
      parts.push('<a class="agent-hint agent-result" href="#/products/' + NM.esc(item.id) + '">' +
        NM.esc(item.title) + ' ' + NM.esc(NM.formatPrice(item.price)) + '</a>');
    });

    log.append('<div class="agent-results">' + parts.join("") + '</div>');
    log.scrollTop(log[0].scrollHeight);
  }

  function ask() {
    if (state.busy) {
      return;
    }

    var input = $("#agent-input");
    var message = input.val().trim();

    if (!message) {
      return;
    }

    if (!auth.isSignedIn()) {
      $("#agent-error").html('登录之后才能问我。<a href="#/login">去登录</a>');
      return;
    }

    state.busy = true;
    input.prop("disabled", true);
    $("#agent-send").prop("disabled", true);
    $("#agent-error").empty();

    appendBubble("user", message);
    input.val("");

    api.post("/api/ai/agent/ask", {
      message: message,
      page: state.page,
      productId: state.productId,
      history: trimHistory(state.history)
    }).then(function (result) {
      appendBubble("assistant", result.answer);
      appendResults(result.results);

      /* 成功才进历史。失败的这一问没答上来，把它记进上下文只会让下一问对着一句
       * 没人回的话发愣。 */
      state.history.push({ role: "user", content: message });
      state.history.push({ role: "assistant", content: result.answer });

      paintQuota(result.remainingToday);
    }, function (error) {
      $("#agent-error").html(NM.inlineError(error));
    }).always(function () {
      state.busy = false;
      input.prop("disabled", false);
      $("#agent-send").prop("disabled", false);
      input.trigger("focus");
    });
  }

  /* 退出登录之后，面板里不能再留着上一个账号的对话和数字。 */
  function onUserChanged() {
    paintComposer();

    if (!auth.isSignedIn()) {
      state.history = [];
      $("#agent-log").empty();
      $("#agent-error").empty();
      $("#agent-quota").empty();
    }

    if (state.open) {
      refreshHints();
    }
  }

  return {
    onRoute: onRoute,
    toggle: toggle,
    close: close,
    ask: ask,
    onUserChanged: onUserChanged
  };
})(jQuery);

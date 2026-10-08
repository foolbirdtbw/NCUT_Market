/* 私信：会话列表、会话详情、发消息、顶栏未读徽标。
 *
 * 会话一定挂在某个商品上，所以「联系卖家」的入口在商品详情页，不在这里。
 * 视图整块用 .html() 重建，事件一律委托在 document 上绑一次（在 app.js 里）。 */
window.messages = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 20;

  /* ---------- 顶栏未读徽标 ---------- */

  function paintBadge(count) {
    var slot = $("#nav-messages");

    if (!slot.length) {
      return;
    }

    slot.find(".nav-badge").remove();

    if (count > 0) {
      // 三位数以上就封顶，徽标是提示不是计数器，撑宽了会把导航挤歪。
      slot.append('<span class="nav-badge">' + (count > 99 ? "99+" : count) + '</span>');
    }
  }

  /* 未读数的刷新点。挂在 auth.onUserChanged 上（在 app.js 里注册），
   * 所以 signIn / signOut / refresh 三条路都会经过这里，不用各写一遍。 */
  function refreshUnread() {
    if (!auth.isSignedIn()) {
      paintBadge(0);
      return;
    }

    api.get("/api/conversations/unread-count").then(function (result) {
      paintBadge(result.count);
    }, function () {
      // 徽标拉不到就当没有未读。它为这一个数字弹一张错误卡，是把提示做成了障碍。
      paintBadge(0);
    });
  }

  /* ---------- 会话列表 ---------- */

  /* 头像位置放昵称首字。没有真头像可传，纯 CSS 圆片比一个默认图省一次请求。 */
  function avatarHtml(nickname) {
    return '<span class="conv-avatar">' + NM.esc(String(nickname || "?").charAt(0)) + '</span>';
  }

  function rowHtml(item) {
    return '<a class="conv-item' + (item.hasUnread ? " is-unread" : "") +
      '" href="#/messages/' + item.id + '">' +
      avatarHtml(item.peerNickname) +
      '<span class="conv-main">' +
      '<span class="conv-title">' + NM.esc(item.peerNickname) + '</span>' +
      '<span class="conv-preview">' +
      NM.esc(item.lastMessagePreview || "还没有消息。") + '</span>' +
      '<span class="conv-product">' + NM.esc(item.productTitle) + '</span>' +
      '</span>' +
      '<span class="conv-meta">' + NM.formatDateTime(item.lastMessageAt) + '</span>' +
      '</a>';
  }

  function showList(query) {
    /* 顶栏那条一直摆着，未登录点进来落在这张卡上。挡在这里而不是靠接口回 401：
     * 401 会先渲染出一张空列表，再被人弹到登录页，中间那一跳很难看。 */
    if (!auth.isSignedIn()) {
      $("#view").html(NM.signInCard("私信"));
      return;
    }

    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>私信</h2>' +
      '<button class="button" data-reload>刷新</button></div>' +
      '<div id="conv-list">' + NM.loading() + '</div>' +
      '</div>');

    api.get("/api/conversations", {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      if (!page.items.length) {
        $("#conv-list").html(NM.empty("还没有会话。在商品详情页点「联系卖家」就能开一个。"));
        return;
      }

      $("#conv-list").html(
        '<div class="conv-list">' + page.items.map(rowHtml).join("") + '</div>' +
        NM.pagerHtml(page, function (target) { return "#/messages?page=" + target; }));
    }, function (error) {
      $("#conv-list").html(NM.errorCard(error));
    });
  }

  /* ---------- 会话详情 ---------- */

  /* 当前会话的 id。从 hash 里现读，不靠闭包——事件是委托的，
   * 处理函数不记得是哪个页面渲染的它。 */
  function currentThreadId() {
    var match = /^#\/messages\/(\d+)/.exec(location.hash);

    return match ? match[1] : null;
  }

  function headHtml(detail) {
    /* productId 为 null 表示商品页打不开了，两种删法都算：真删掉的，和卖家删掉的。
     * 这时候不给链接——跳过去只会看到 404，标题用的是建会话时冻结下来的那份。 */
    var product = detail.productId
      ? '<a href="#/products/' + detail.productId + '">' + NM.esc(detail.productTitle) + '</a>'
      : NM.esc(detail.productTitle) + '<span class="muted"> · 商品已删除</span>';

    return '<div class="card thread-head">' +
      (detail.productThumbnailUrl
        ? '<img class="thread-thumb" src="' + NM.esc(detail.productThumbnailUrl) + '" alt="">'
        : '') +
      '<div class="thread-head-main">' +
      '<div class="thread-peer">' + NM.esc(detail.peerNickname) + '</div>' +
      '<div class="thread-product">' + product + '</div>' +
      '</div>' +
      '<a class="button" href="#/messages">返回列表</a>' +
      '</div>';
  }

  /* 交易面板：一句话说清现在到哪一步了，再加一个「该我做的动作」。
   *
   * 可操作性完全由服务端给的事实推出来，前端不预判——点了不该点的，服务端回一句中文 409。
   * 这正是 ConversationTradeResponse 存在的理由：服务端给事实，客户端判按钮。
   *
   * detail.trade 为 null 表示商品已经整个没了（卖家自己下架或标记售出之后删掉的，那种是真删），
   * 整块不出现。反过来，商品**走过平台交易**再被卖家删掉时这块照常出现——服务端只把它标成
   * 已删除，行还留着，交易面板和确认过程双方都还看得到，那正是这么设计的原因。
   * 注意这是两个独立的信号：productId 为 null 只说明商品页打不开了，不代表没有交易记录。
   * 不是本交易会话的那些线程，服务端把 acceptedAt 抹成 null，所以「这笔交易是不是我谈成的」
   * 就等价于 acceptedAt != null——别人拿下的商品在他们自己的会话里看起来和在售没区别。 */
  /* 第三个参数给「拒绝/撤回」这类不由我主动推进的动作。两个按钮并排时都是主色的话，
     点哪个就得先把字读完——次按钮把「接受」让出来。 */
  function tradeButton(action, label, secondary) {
    return '<button class="button' + (secondary ? "" : " button-primary") +
      '" data-action="trade-' + action + '">' + NM.esc(label) + '</button>';
  }

  function tradeHtml(detail, myId) {
    var trade = detail.trade;

    if (!trade) {
      return "";
    }

    // Published=2, Sold=3, InTransaction=5，和 STATUSES 同一套枚举值。
    var status = trade.productStatus;
    var mine = trade.proposedById === myId;
    var note;
    var action = "";

    if (status === 2) {
      if (!trade.proposedAt) {
        note = "还没开始交易。任一方发起、另一方接受之后，商品进入交易中。";
        action = tradeButton("propose", "发起交易");
      } else if (mine) {
        note = "你发起了交易，等待对方确认。一天内没有回应会自动取消。";
        action = tradeButton("withdraw", "撤回交易", true);
      } else {
        note = "对方发起了交易。";
        action = tradeButton("accept", "接受交易") + tradeButton("reject", "拒绝交易", true);
      }
    } else if (status === 5 && trade.acceptedAt) {
      note = "交易进行中。双方各确认一次就完成了。";

      if (trade.buyerId === myId) {
        action = trade.buyerConfirmedAt
          ? '<span class="muted">你已确认收货，等待卖方确认收款。</span>'
          : tradeButton("receipt", "确认收货");
      } else {
        action = trade.sellerConfirmedAt
          ? '<span class="muted">你已确认收款，等待买方确认收货。</span>'
          : tradeButton("payment", "确认收款");
      }
    } else if (status === 5) {
      note = "这件商品正在和别人交易中。";
    } else if (status === 3) {
      note = trade.acceptedAt ? "交易已完成。" : "这件商品已经卖出了。";
    } else {
      // 草稿和已下架没有交易可谈，这一块整个不出现。
      return "";
    }

    return '<div class="card trade-panel">' +
      '<div class="trade-line">' + NM.statusBadge(status) +
      '<span class="trade-note">' + NM.esc(note) + '</span></div>' +
      (action ? '<div class="trade-actions">' + action + '</div>' : "") +
      '<p id="trade-error"></p>' +
      '</div>';
  }

  function messageHtml(message, myId) {
    var mine = message.senderId === myId;

    return '<div class="bubble ' + (mine ? "bubble-mine" : "bubble-peer") + '">' +
      // 换行交给 CSS 的 white-space: pre-wrap，不在这里替成 <br>——
      // 替了就等于把用户输入又拼进了一次 HTML。
      '<div class="bubble-text">' + NM.esc(message.content) + '</div>' +
      '<div class="bubble-meta">' +
      (mine ? "" : NM.esc(message.senderNickname) + " · ") +
      NM.formatDateTime(message.createdAt) + '</div>' +
      '</div>';
  }

  function showThread(id, query) {
    // 同 showList：会话链接未登录时也能直接敲进来。
    if (!auth.isSignedIn()) {
      $("#view").html(NM.signInCard("私信"));
      return;
    }

    $("#view").html(NM.loading());

    api.get("/api/conversations/" + id, {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (detail) {
      var myId = (auth.user() || {}).id;
      var messages = detail.messages;

      $("#view").html(
        headHtml(detail) +
        tradeHtml(detail, myId) +
        '<div class="card">' +
        '<div class="thread-messages" id="thread-messages">' +
        (messages.items.length
          ? messages.items.map(function (message) { return messageHtml(message, myId); }).join("")
          : NM.empty("还没有消息。打个招呼吧。")) +
        '</div>' +
        /* 接口给的是最新一页。往上翻旧消息这一轮不做，但要说明白少了什么，
         * 不能让一页只显示 20 条而看起来像全部。 */
        (messages.totalPages > 1
          ? '<p class="muted thread-more">只显示了最近 ' + messages.items.length +
            ' 条，这个会话共 ' + messages.totalCount + ' 条。</p>'
          : '') +
        '<form id="message-form" class="thread-form">' +
        '<textarea id="message-input" rows="2" maxlength="500" ' +
        'placeholder="写点什么…（最多 500 字）"></textarea>' +
        '<button class="button button-primary" type="submit">发送</button>' +
        '</form>' +
        '<div id="message-error"></div>' +
        '</div>');

      scrollToBottom();

      // 打开即算读过。服务端只盖章自己那一侧，不会替对方已读。
      markRead(id);
    }, function (error) {
      $("#view").html(NM.errorCard(error));
    });
  }

  function scrollToBottom() {
    var box = $("#thread-messages");

    if (box.length) {
      box.scrollTop(box[0].scrollHeight);
    }
  }

  function markRead(id) {
    api.post("/api/conversations/" + id + "/read", null).then(function () {
      refreshUnread();
    }, function () {
      // 盖章失败不影响正在看的这一页，只是徽标会多留一会儿。
    });
  }

  function send() {
    var id = currentThreadId();
    var content = $("#message-input").val().trim();

    if (!content) {
      $("#message-error").html(NM.inlineError({ message: "请写点什么。" }));
      return;
    }

    $("#message-error").empty();

    api.post("/api/conversations/" + id + "/messages", { content: content }).then(function () {
      // 重新拉一次详情，而不是把返回值追加进 DOM——和服务端状态对齐的唯一做法。
      showThread(id, {});
      refreshUnread();
    }, function (error) {
      $("#message-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 从商品详情页开一个会话 ---------- */

  /* 接口是 find-or-create 的，所以重复点「联系卖家」不会开出第二个会话，
   * 只是回到原来那个。 */
  function start(productId) {
    $("#detail-error").html(NM.loading());

    api.post("/api/conversations", { productId: Number(productId) }).then(function (conversation) {
      location.hash = "#/messages/" + conversation.id;
    }, function (error) {
      // 联系自己的商品、或者商品已经看不见了，中文原因都在这里。
      $("#detail-error").html(NM.inlineError(error));
    });
  }

  /* ---------- 交易动作 ---------- */

  /* 「撤回」和「拒绝」是同一个后端的两个读法，两行同一个 URL。分成两个名字纯粹是为了让
     确认弹窗只挂在撤回上——它是唯一一个一键生效、不可逆、还会推通知给对方的动作，
     而拒绝紧挨着「接受」，后果却轻（对方马上能再发起）。名字对得上按钮，URL 对得上接口。 */
  var TRADE_PATHS = {
    "trade-propose": "",
    "trade-accept": "/accept",
    "trade-withdraw": "/cancel",
    "trade-reject": "/cancel",
    "trade-receipt": "/receipt",
    "trade-payment": "/payment"
  };

  /* 六个按钮共用一个执行器：POST 一下，失败就把中文原因写回面板。
   *
   * 成功之后重拉整个会话而不是就地改 DOM——和服务端状态对齐的唯一做法，
   * 和发消息那条路一样。重拉还会顺带把面板本身换成新状态，不用单独维护一套状态机。
   *
   * onDone 由 app.js 传进来（通知徽标刷新）。这里不直接调 notifications：
   * 这个仓库的模块之间不互相调用，接线统一在 app.js。 */
  function tradeAction(action, onDone) {
    var id = currentThreadId();
    var path = TRADE_PATHS[action];

    if (!id || path === undefined) {
      return;
    }

    if (action === "trade-withdraw" &&
        !confirm("撤回之后对方会收到通知。确定撤回这次交易提议吗？")) {
      return;
    }

    $("#trade-error").empty();

    api.post("/api/conversations/" + id + "/transaction" + path, null).then(function () {
      showThread(id, {});

      if (onDone) {
        onDone();
      }
    }, function (error) {
      // 「商品状态刚刚变了，请刷新重试。」这类都在这里显示。
      $("#trade-error").html(NM.inlineError(error));
    });
  }

  return {
    showList: showList,
    showThread: showThread,
    send: send,
    start: start,
    tradeAction: tradeAction,
    refreshUnread: refreshUnread
  };
})(jQuery);

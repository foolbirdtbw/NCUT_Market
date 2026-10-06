/* 通知：列表、顶栏未读徽标。
 *
 * 通知全部由服务端在状态变更的同一笔提交里写入（交易开始、对方确认、提议过期……），
 * 这里只有读和「标记已读」两件事，没有「新建」入口，也造不出假历史。
 *
 * 整块结构照 messages.js：视图整块用 .html() 重建，事件委托在 document 上（在 app.js 里）。 */
window.notifications = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 20;

  /* ---------- 顶栏未读徽标 ---------- */

  /* 和私信徽标同一个写法：数字封顶在 99+，撑宽了会把导航挤歪。 */
  function paintBadge(count) {
    var slot = $("#nav-notifications");

    if (!slot.length) {
      return;
    }

    slot.find(".nav-badge").remove();

    if (count > 0) {
      slot.append('<span class="nav-badge">' + (count > 99 ? "99+" : count) + '</span>');
    }
  }

  /* 刷新点：登录状态变化（在 app.js 里注册）+ 首屏 + 交易动作之后。
   * 没有轮询——和私信徽标保持一致，只在明确的时刻拉一次。 */
  function refreshUnread() {
    if (!auth.isSignedIn()) {
      paintBadge(0);
      return;
    }

    api.get("/api/notifications/unread-count").then(function (result) {
      paintBadge(result.count);
    }, function () {
      // 拉不到就当没有未读。为一个数字弹错误卡，是把提示做成了障碍。
      paintBadge(0);
    });
  }

  /* ---------- 列表 ---------- */

  /* 每种通知换一个图标位置的字符。纯装饰，认不出类型也不会少信息——标题和正文都在。
   * 索引即 NotificationType 的枚举值，0 号留空。 */
  var GLYPHS = ["", "🗑️", "✅", "⏸️", "🤝", "🔔", "↩️"];

  function rowHtml(item) {
    /* 关联商品的链接只在商品还在时给。商品被硬删除后 relatedProductId 仍是原值，
     * 跳过去只会 404——点击即标记已读，所以这里用 div 而不是 a。 */
    return '<div class="notice-item' + (item.isRead ? "" : " is-unread") +
      '" data-action="open-notification" data-notification-id="' + item.id + '">' +
      '<span class="notice-glyph">' + (GLYPHS[item.type] || "🔔") + '</span>' +
      '<span class="notice-main">' +
      '<span class="notice-title">' + NM.esc(item.title) + '</span>' +
      '<span class="notice-content">' + NM.esc(item.content) + '</span>' +
      '<span class="notice-meta">' + NM.formatDateTime(item.createdAt) +
      (item.relatedProductId
        ? ' · <a href="#/products/' + item.relatedProductId + '">查看商品</a>'
        : '') +
      '</span>' +
      '</span>' +
      '</div>';
  }

  function showList(query) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>通知</h2>' +
      '<button class="button" data-reload>刷新</button></div>' +
      '<div id="notice-list">' + NM.loading() + '</div>' +
      '</div>');

    api.get("/api/notifications", {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      if (!page.items.length) {
        $("#notice-list").html(NM.empty("还没有通知。"));
        return;
      }

      $("#notice-list").html(
        '<div class="notice-list">' + page.items.map(rowHtml).join("") + '</div>' +
        NM.pagerHtml(page, function (target) { return "#/notifications?page=" + target; }));
    }, function (error) {
      $("#notice-list").html(NM.errorCard(error));
    });
  }

  /* 点开一条 = 标记已读。不回写 DOM，只把徽标和那一条的样式改掉。
   * 整页重拉会把用户正在看的位置弹走，这里没必要付那个代价。 */
  function open(id) {
    var row = $("[data-notification-id='" + id + "']");

    if (!row.hasClass("is-unread")) {
      return;
    }

    api.post("/api/notifications/" + id + "/read", null).then(function () {
      row.removeClass("is-unread");
      refreshUnread();
    }, function () {
      // 标记失败就让它留着未读。看内容不受影响，不打断。
    });
  }

  return {
    showList: showList,
    open: open,
    refreshUnread: refreshUnread
  };
})(jQuery);

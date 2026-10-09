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
    /* 点击即标记已读，所以整行是 div 而不是 a——商品链接挪到了组头上，那里才是它的位置。 */
    return '<div class="notice-item' + (item.isRead ? "" : " is-unread") +
      '" data-action="open-notification" data-notification-id="' + item.id + '">' +
      '<span class="notice-glyph">' + (GLYPHS[item.type] || "🔔") + '</span>' +
      '<span class="notice-main">' +
      '<span class="notice-title">' + NM.esc(item.title) + '</span>' +
      '<span class="notice-content">' + NM.esc(item.content) + '</span>' +
      '<span class="notice-meta">' + NM.formatDateTime(item.createdAt) + '</span>' +
      '</span>' +
      '</div>';
  }

  /* 同一个商品的通知归成一组。
   *
   * 分组放在这里而不是服务端：服务端列表仍是扁平的、按时间倒序、按条分页，一个字没改。
   * 代价是一个商品的通知有可能被页边界切开，出现相邻两组同名商品——一页 20 条、一笔交易
   * 最多 6 条，实际很少见；换来的是不用在 SQL 里分组，也不用为分组另算一遍总数。
   *
   * relatedProductId 为 null 表示商品已经被硬删除（外键 SET NULL），那种通知各自成组。 */
  function groupItems(items) {
    var order = [];
    var groups = {};

    items.forEach(function (item) {
      var key = item.relatedProductId || ("n" + item.id);

      if (!groups[key]) {
        groups[key] = { productId: item.relatedProductId, title: item.productTitle, items: [] };
        order.push(key);
      }

      groups[key].items.push(item);
    });

    return order.map(function (key) { return groups[key]; });
  }

  function groupHtml(group) {
    /* 组头。商品还在就是能点进去的名字；不在了就说明白，而不是给一个跳过去 404 的链接。
     * 删除按钮在组头上，组内每条照旧点开即已读——两种动作各占一层，不会互相吃掉点击。 */
    var head = group.productId
      ? '<a href="#/products/' + group.productId + '">' + NM.esc(group.title) + '</a>'
      : '<span class="muted">已删除的商品</span>';

    /* 商品没了就没有产品 id 可以按组删，退化成删这一条——那一组本来也只有一条。 */
    var scope = group.productId
      ? ' data-product-id="' + group.productId + '"'
      : ' data-notification-id="' + group.items[0].id + '"';

    return '<div class="notice-group">' +
      '<div class="notice-group-head">' + head +
      '<button class="button button-danger" data-action="delete-notice"' + scope + '>删除</button>' +
      '</div>' +
      group.items.map(rowHtml).join("") +
      '</div>';
  }

  function showList(query) {
    /* 顶栏那条一直摆着，未登录点进来落在这张卡上。挡在这里而不是靠接口回 401：
     * 401 会先渲染出一张空列表，再被人弹到登录页，中间那一跳很难看。 */
    if (!auth.isSignedIn()) {
      $("#view").html(NM.signInCard("通知"));
      return;
    }

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
        '<div class="notice-list">' + groupItems(page.items).map(groupHtml).join("") + '</div>' +
        NM.pagerHtml(page, function (target) { return "#/notifications?page=" + target; }));
    }, function (error) {
      $("#notice-list").html(NM.errorCard(error));
    });
  }

  /* 点开一条 = 标记已读。不回写 DOM，只把徽标和那一条的样式改掉。
   * 整页重拉会把用户正在看的位置弹走，这里没必要付那个代价。
   *
   * 选择器钉住 .notice-item：商品已经没了的那一组，组头的删除按钮上也带着同一个
   * data-notification-id，不钉的话这一句会同时选中按钮。 */
  function open(id) {
    var row = $(".notice-item[data-notification-id='" + id + "']");

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

  /* 组头那个删除按钮。两个参数里只会来一个：按商品（整组）或按单条（商品已经没了的那组）。
   * 真删，不能恢复，所以确认框的措辞得把这一点说出来。
   *
   * 删完重新路由当前页，而不是就地摘 DOM：删掉整组会让这一页少好几行，页码和总数都跟着错位，
   * 重拉一次是唯一能和服务端对齐的做法——和私信那边发完消息重拉详情同一个理由。
   *
   * route() 在 app.js 的闭包里拿不到，所以手工触发一次 hashchange 让它自己走：hash 没变，
   * 挂着的那次不会自己来。 */
  function remove(productId, notificationId) {
    if (!confirm("删除之后这些通知就没了，不能恢复。确定删除吗？")) {
      return;
    }

    var call = productId
      ? api.del("/api/notifications/product/" + productId)
      : api.del("/api/notifications/" + notificationId);

    call.then(function () {
      $(window).trigger("hashchange");
      refreshUnread();
    }, function (error) {
      $("#notice-list").html(NM.errorCard(error));
    });
  }

  return {
    showList: showList,
    open: open,
    remove: remove,
    refreshUnread: refreshUnread
  };
})(jQuery);

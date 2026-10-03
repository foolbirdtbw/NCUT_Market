/* 站点公告：顶部条、公告页、管理员的发布/删除表单。
 *
 * 顶部条只显示最新一条，关掉之后记在 localStorage 里，刷新不再弹——
 * 但那是按公告 id 记的，所以发一条新的照样会出现。 */
window.announcements = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 10;
  var DISMISS_KEY = "ncut.announcement.dismissed";

  /* localStorage 在隐私模式下会直接抛，不是返回 null。读不到就当没关过。 */
  function readDismissed() {
    try { return Number(localStorage.getItem(DISMISS_KEY)) || 0; } catch (e) { return 0; }
  }

  function writeDismissed(id) {
    try { localStorage.setItem(DISMISS_KEY, String(id)); } catch (e) { /* 关不掉也比白屏强。 */ }
  }

  /* 是不是管理员。role 是 users 表里读出来的，Admin = 2。
   * 这里只是决定要不要把表单画出来，真正的判定在服务端。 */
  function isAdmin(current) {
    return !!current && current.role === 2;
  }

  /* ---------- 顶部条 ---------- */

  function refreshBar() {
    var bar = $("#announcement-bar");

    if (!bar.length) {
      return;
    }

    // 不另开 /latest：按发布时间倒序取一页，第一条就是最新的。
    api.get("/api/announcements", { page: 1, pageSize: 1 }).then(function (page) {
      var latest = page.items[0];

      if (!latest || latest.id === readDismissed()) {
        bar.empty();
        return;
      }

      bar.html(
        '<div class="wrap announcement-inner">' +
        '<span class="announcement-text">' +
        '<strong>' + NM.esc(latest.title) + '</strong>' + NM.esc(latest.content) + '</span>' +
        '<a class="announcement-more" href="#/announcements">全部公告</a>' +
        '<button class="button announcement-close" data-action="announcement-dismiss" ' +
        'data-announcement-id="' + latest.id + '" aria-label="关闭公告">×</button>' +
        '</div>');
    }, function () {
      // 公告拉不到就不显示。它是附加信息，不该在顶栏上摆一条错误。
    });
  }

  function dismiss(id) {
    writeDismissed(Number(id));
    $("#announcement-bar").empty();
  }

  /* ---------- 公告页 ---------- */

  function itemHtml(item, admin) {
    return '<li class="announcement-item">' +
      '<div class="announcement-head">' +
      '<strong>' + NM.esc(item.title) + '</strong>' +
      '<span class="muted">' + NM.formatDateTime(item.publishedAt) +
      (item.expiredAt ? ' · 截止 ' + NM.formatDateTime(item.expiredAt) : '') + '</span>' +
      (admin
        ? '<button class="button button-danger" data-action="delete-announcement" ' +
          'data-announcement-id="' + item.id + '">删除</button>'
        : '') +
      '</div>' +
      '<p class="announcement-body">' + NM.esc(item.content) + '</p>' +
      '</li>';
  }

  function adminFormHtml() {
    return '<div class="card form-card">' +
      '<h2>发布公告</h2>' +
      '<form id="announcement-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="announcement-title">标题</label>' +
      '<input type="text" id="announcement-title" maxlength="100" required>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="announcement-content">正文</label>' +
      '<textarea id="announcement-content" rows="4" maxlength="5000" required></textarea>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="announcement-expired">过期时间</label>' +
      '<input type="datetime-local" id="announcement-expired">' +
      '<span class="hint">留空表示一直显示。</span>' +
      '</div>' +
      '<div id="announcement-error"></div>' +
      '<button class="button button-primary" type="submit">发布</button>' +
      '</form></div>';
  }

  function showList(query) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>站点公告</h2>' +
      '<button class="button" data-reload>刷新</button></div>' +
      '<div id="announcement-list">' + NM.loading() + '</div>' +
      '</div>');

    /* 是不是管理员要等 /me 回来才算数：本地缓存里那份可能是提权之前的，
     * 刚 UPDATE 完 role 再刷新页面就会撞上。公告页访问很少，多发一次请求换掉这个坑划算。 */
    auth.refresh().then(function (current) {
      var admin = isAdmin(current);

      if (admin) {
        $("#view").append(adminFormHtml());
      }

      loadList(query, admin);
    });
  }

  function loadList(query, admin) {
    api.get("/api/announcements", {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      if (!page.items.length) {
        $("#announcement-list").html(NM.empty("现在没有公告。"));
        return;
      }

      $("#announcement-list").html(
        '<ul class="announcement-list">' +
        page.items.map(function (item) { return itemHtml(item, admin); }).join("") +
        '</ul>' +
        NM.pagerHtml(page, function (target) { return "#/announcements?page=" + target; }));
    }, function (error) {
      $("#announcement-list").html(NM.errorCard(error));
    });
  }

  /* ---------- 管理员的两个动作 ---------- */

  function submitPublish() {
    var title = $("#announcement-title").val().trim();
    var content = $("#announcement-content").val().trim();
    var expiredAt = $("#announcement-expired").val();

    if (!title || !content) {
      $("#announcement-error").html(NM.inlineError({ message: "标题和正文都要填。" }));
      return;
    }

    $("#announcement-error").empty();

    api.post("/api/announcements", {
      title: title,
      content: content,

      /* datetime-local 给的是 "2026-10-05T12:00"，没有时区后缀，正是库里的口径——
       * 服务端把它当北京时间的墙上时钟存，这里不做任何转换，转了反而差 8 小时。 */
      expiredAt: expiredAt || null
    }).then(function () {
      refreshBar();
      showList({});
    }, function (error) {
      $("#announcement-error").html(NM.inlineError(error));
    });
  }

  function deleteAnnouncement(id) {
    if (!confirm("删除之后不能恢复。确定删掉这条公告吗？")) {
      return;
    }

    api.del("/api/announcements/" + id).then(function () {
      refreshBar();
      showList({});
    }, function (error) {
      $("#announcement-list").prepend(NM.inlineError(error));
    });
  }

  return {
    refreshBar: refreshBar,
    dismiss: dismiss,
    showList: showList,
    submitPublish: submitPublish,
    deleteAnnouncement: deleteAnnouncement
  };
})(jQuery);

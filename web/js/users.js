/* 管理员用户列表 + 发密码重置码。
 *
 * 这一页存在的理由很具体：注册只要用户名和密码，没有邮箱也没有手机号，所以谁忘了密码
 * 都没有自助的路。管理员线下核验身份（学生证、学号）之后在这里生成一串一次性重置码，
 * 用户自己去 #/forgot 用它换一个新密码。管理员从头到尾不知道用户最后设的密码。
 *
 * 门禁和 dictionaries.js 一样只做在界面上，真正的判定在服务端（IUserService 里每次
 * 调用都查一遍 users.role）。 */
window.users = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 20;

  /* 当前这一屏的数据和查询条件。事件是委托在 document 上的，处理函数不在渲染时的闭包里，
   * 所以这些只能留在模块里。 */
  var currentUsers = [];
  var currentKeyword = "";
  var currentPage = 1;

  /* 是不是管理员。role 是 users 表里读出来的，Admin = 2。 */
  function isAdmin(current) {
    return !!current && current.role === 2;
  }

  function forbiddenHtml() {
    return '<div class="card state-card"><h1>没有权限</h1>' +
      '<p class="muted">用户管理只有管理员能进。</p>' +
      '<a class="button" href="#/products">去逛商品</a></div>';
  }

  /* 门禁。必须等 /me 回来才算数：本地缓存里那份 role 可能是提权之前的。 */
  function gate(render) {
    auth.refresh().then(function (current) {
      if (!isAdmin(current)) {
        $("#view").html(forbiddenHtml());
        return;
      }

      render();
    });
  }

  /* 关键字和页码都放在 hash 里，所以刷新和后退能回到同一屏。 */
  function goTo(keyword, page) {
    var target = "#/users?keyword=" + encodeURIComponent(keyword) + "&page=" + page;

    /* 哈希没变就不会触发 hashchange（比如同样的关键字连按两次搜索），
     * 那就自己重拉一次列表。 */
    if (location.hash === target) {
      currentKeyword = keyword;
      currentPage = page;
      loadUsers();
      return;
    }

    location.hash = target;
  }

  function searchFormHtml() {
    return '<div class="card form-card">' +
      '<h2>找用户</h2>' +
      '<form id="user-search-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="user-keyword">用户名或昵称</label>' +
      '<input type="text" id="user-keyword" maxlength="50" value="' + NM.esc(currentKeyword) + '">' +
      '<span class="hint">不用打全，包含就行。留空列出全部用户。</span>' +
      '</div>' +
      '<div class="action-bar">' +
      '<button class="button button-primary" type="submit">搜索</button>' +
      '<button class="button" type="button" data-action="clear-user-search">清空</button>' +
      '</div>' +
      '</form></div>';
  }

  function showUsers(query) {
    var incoming = query || {};

    currentKeyword = incoming.keyword || "";
    currentPage = parseInt(incoming.page, 10) || 1;
    currentUsers = [];

    $("#view").html(NM.loading());

    gate(function () {
      $("#view").html(
        /* 重置码单独一个槽，不跟列表一起重画：发完码要刷新列表（那行的「待使用」要出现），
         * 码还留在屏幕上等管理员抄下来。 */
        '<div id="reset-slot"></div>' +
        searchFormHtml() +
        '<div class="card">' +
        '<div class="card-head"><h2>用户</h2>' +
        '<button class="button" data-reload>刷新</button></div>' +
        '<div id="list">' + NM.loading() + '</div>' +
        '</div>');

      loadUsers();
    });
  }

  function loadUsers() {
    api.get("/api/users", {
      keyword: currentKeyword,
      page: currentPage,
      pageSize: PAGE_SIZE
    }).then(function (result) {
      currentUsers = result.items;

      if (!result.items.length) {
        $("#list").html(NM.empty(currentKeyword ? "没有匹配的用户。" : "还没有用户。"));
        return;
      }

      $("#list").html(
        '<div class="table-scroll"><table><thead><tr>' +
        '<th>ID</th><th>用户名</th><th>昵称</th><th>角色</th><th>状态</th>' +
        '<th>注册时间</th><th>重置码</th><th>操作</th>' +
        '</tr></thead><tbody>' +
        result.items.map(rowHtml).join("") +
        '</tbody></table></div>' +
        NM.pagerHtml(result, function (target) {
          return "#/users?keyword=" + encodeURIComponent(currentKeyword) + "&page=" + target;
        }));
    }, function (error) {
      $("#list").html(NM.errorCard(error));
    });
  }

  function rowHtml(user) {
    return '<tr>' +
      '<td>' + user.id + '</td>' +
      '<td>' + NM.esc(user.username) + '</td>' +
      '<td>' + NM.esc(user.nickname) + '</td>' +
      '<td>' + (user.role === 2 ? "管理员" : "普通用户") + '</td>' +
      /* 账号状态和商品状态是两张不同的枚举表，值还会撞车，所以走 userStatusText 而不是 statusText。 */
      '<td>' + NM.userStatusText(user.status) + '</td>' +
      '<td>' + NM.formatDateTime(user.createdAt) + '</td>' +
      '<td>' + (user.passwordResetExpiresAt
        ? '<span class="badge badge-pending">待使用</span> ' +
          '<span class="muted">至 ' + NM.formatDateTime(user.passwordResetExpiresAt) + '</span>'
        : "—") + '</td>' +
      '<td>' +
      '<button type="button" class="button" data-action="reset-password" ' +
      'data-user-id="' + user.id + '">重置密码</button>' +
      '</td>' +
      '</tr>';
  }

  /* 从当前页那份数据里找用户名，而不是把它塞进 DOM 再读回来。
   * 和 dictionaries.js 里 currentCategories 一个路子。 */
  function issueResetCode(id) {
    var found = currentUsers.filter(function (item) {
      return String(item.id) === String(id);
    })[0];

    if (!found) {
      return;
    }

    if (!confirm("给「" + found.username + "」生成一个重置码？\n" +
      "生成之后，之前发给他但还没用的码会立刻失效。")) {
      return;
    }

    api.post("/api/users/" + found.id + "/reset-password").then(function (result) {
      $("#reset-slot").html(codeCardHtml(found.username, result));

      /* 重拉列表：这一行的「重置码」列该显示成待使用了。码在另一个槽里，不受影响。 */
      loadUsers();
    }, function (error) {
      $("#reset-slot").html(NM.inlineError(error));
    });
  }

  function codeCardHtml(username, result) {
    return '<div class="card reset-card">' +
      '<h2>重置码已生成</h2>' +
      '<p>把这串码交给 <strong>' + NM.esc(username) + '</strong>，' +
      '让他到「忘记密码」页自己设新密码。它只能用一次，' +
      NM.formatDateTime(result.expiresAt) + ' 之前有效。</p>' +
      '<div class="reset-code" id="reset-code">' + NM.esc(result.resetCode) + '</div>' +
      '<p class="muted">刷新页面或再搜一次，这串码就不再显示了——库里存的是它的摘要，' +
      '服务端自己也拿不回明文。弄丢了就再生成一个。</p>' +
      '<div class="action-bar">' +
      '<button class="button button-primary" type="button" data-action="copy-reset-code">复制</button>' +
      '<button class="button" type="button" data-action="dismiss-reset-code">知道了</button>' +
      '</div></div>';
  }

  /* 复制。非安全上下文、无痕模式或权限被拒时 navigator.clipboard 要么不存在要么 reject，
   * 两条路都安静地算了——码本身就是可选中的文本，复制只是顺手。 */
  function copyResetCode() {
    var code = $("#reset-code").text();

    if (!code || !navigator.clipboard) {
      return;
    }

    navigator.clipboard.writeText(code).then(function () {
      $("[data-action='copy-reset-code']").text("已复制");
    }, function () { });
  }

  function dismissResetCode() {
    $("#reset-slot").empty();
  }

  function submitSearch() {
    goTo($("#user-keyword").val().trim(), 1);
  }

  function clearSearch() {
    $("#user-keyword").val("");
    goTo("", 1);
  }

  return {
    showUsers: showUsers,
    submitSearch: submitSearch,
    clearSearch: clearSearch,
    issueResetCode: issueResetCode,
    copyResetCode: copyResetCode,
    dismissResetCode: dismissResetCode
  };
})(jQuery);

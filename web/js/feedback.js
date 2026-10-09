/* 问题反馈板：所有人能看，登录用户能发、能点赞，管理员能改状态、能删。
 *
 * 列表由服务端按赞同数排序，但点赞之后只改那一颗按钮、不重新拉列表——一点就整体重排的话，
 * 刚点的那条会从指尖底下跳走。新的次序下次进页面时才生效。 */
window.feedback = (function ($) {
  "use strict";

  var NM = window.NM;

  var PAGE_SIZE = 10;

  /* 徽标配色，按枚举值索引，缺的落到默认灰。和 ui.js 里那两张文字表分开放：
   * 那边只管把人话翻出来，颜色是这一页自己的事。 */
  var KIND_BADGES = { 1: "badge-kind-bug", 2: "badge-kind-feature" };
  var STATUS_BADGES = { 1: "badge-open", 2: "badge-accepted", 3: "badge-done", 4: "badge-rejected" };

  /* 是不是管理员。role 是 users 表里读出来的，Admin = 2。
   * 这里只是决定要不要把管理控件画出来，真正的判定在服务端。 */
  function isAdmin(current) {
    return !!current && current.role === 2;
  }

  function kindHtml(kind) {
    return '<span class="badge ' + (KIND_BADGES[kind] || "badge") + '">' +
      NM.esc(NM.feedbackKindText(kind)) + '</span>';
  }

  function statusHtml(status) {
    return '<span class="badge ' + (STATUS_BADGES[status] || "badge") + '">' +
      NM.esc(NM.feedbackStatusText(status)) + '</span>';
  }

  /* 改完状态、删完一条之后回哪一页。页码在 hash 里，而事件是委托的，处理函数不在渲染时的
   * 闭包里，所以现读——app.js 的 matchProductId 同一个理由。 */
  function currentPage() {
    var match = /[?&]page=(\d+)/.exec(location.hash);

    return { page: match ? match[1] : 1 };
  }

  /* ---------- 渲染 ---------- */

  function itemHtml(item, admin, signedIn) {
    return '<li class="feedback-item">' +

      /* 没登录就禁用，而不是让它点出个 401 再被弹去登录页：看得到票数、点不动，
       * 比点一下才发现要登录更好解释。 */
      '<button class="button feedback-vote' + (item.hasVoted ? " is-voted" : "") + '"' +
      ' data-action="vote-feedback" data-feedback-id="' + item.id + '"' +
      ' aria-pressed="' + (item.hasVoted ? "true" : "false") + '" aria-label="赞同"' +
      (signedIn ? "" : " disabled") + '>' +
      '<span class="vote-arrow">▲</span>' +
      '<span class="vote-count">' + item.voteCount + '</span>' +
      '</button>' +
      '<div class="feedback-main">' +
      '<div class="feedback-head">' +
      kindHtml(item.kind) + statusHtml(item.status) +
      '<span class="muted">' +
      (item.authorNickname ? NM.esc(item.authorNickname) : "匿名") +
      ' · ' + NM.formatDateTime(item.createdAt) + '</span>' +
      '</div>' +
      '<p class="feedback-body">' + NM.esc(item.content) + '</p>' +
      (admin ? adminActionsHtml(item) : "") +
      '</div></li>';
  }

  function adminActionsHtml(item) {
    return '<div class="feedback-actions">' +
      '<select data-action="set-feedback-status" data-feedback-id="' + item.id + '"' +
      ' aria-label="处理状态">' + NM.feedbackStatusOptions(item.status) + '</select>' +
      '<button class="button button-danger" data-action="delete-feedback"' +
      ' data-feedback-id="' + item.id + '">删除</button>' +
      '</div>';
  }

  function composeFormHtml() {
    return '<div class="card form-card">' +
      '<h2>提一条反馈</h2>' +
      '<form id="feedback-form" novalidate>' +
      '<div class="field-stack">' +
      '<label for="feedback-kind">类型</label>' +
      '<select id="feedback-kind">' +
      '<option value="1">' + NM.esc(NM.feedbackKindText(1)) + '</option>' +
      '<option value="2">' + NM.esc(NM.feedbackKindText(2)) + '</option>' +
      '</select>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label for="feedback-content">内容</label>' +
      '<textarea id="feedback-content" rows="3" maxlength="1000" required></textarea>' +
      '<span class="hint">遇到的问题，或者想要的功能。最多 1000 个字。</span>' +
      '</div>' +
      '<div class="field-stack">' +
      '<label><input type="checkbox" id="feedback-anonymous"> 匿名发布</label>' +
      '<span class="hint">勾上之后列表里不显示你的昵称（后台仍记录发布者）。</span>' +
      '</div>' +
      '<div id="feedback-error"></div>' +
      '<button class="button button-primary" type="submit">发布</button>' +
      '</form></div>';
  }

  function showList(query) {
    $("#view").html(
      '<div class="card">' +
      '<div class="card-head"><h2>问题反馈</h2>' +
      '<button class="button" data-reload>刷新</button></div>' +
      '<p class="muted">遇到的 bug、想要的功能都可以提，按赞同数排。</p>' +
      '<div id="feedback-list">' + NM.loading() + '</div>' +
      '</div>' +
      '<div id="feedback-compose"></div>');

    /* 登录状态要等 /me 回来才算数：本地缓存里那份可能是退出之前的。
     * refresh() 不 reject（拉不到就按未登录收场），所以一个回调够用。 */
    auth.refresh().then(function (current) {
      if (current) {
        $("#feedback-compose").html(composeFormHtml());
      }

      loadList(query, isAdmin(current), !!current);
    });
  }

  function loadList(query, admin, signedIn) {
    api.get("/api/feedback", {
      page: query.page || 1,
      pageSize: PAGE_SIZE
    }).then(function (page) {
      if (!page.items.length) {
        $("#feedback-list").html(NM.empty("还没有人提过反馈。"));
        return;
      }

      $("#feedback-list").html(
        '<ul class="feedback-list">' +
        page.items.map(function (item) { return itemHtml(item, admin, signedIn); }).join("") +
        '</ul>' +
        NM.pagerHtml(page, function (target) { return "#/feedback?page=" + target; }));
    }, function (error) {
      $("#feedback-list").html(NM.errorCard(error));
    });
  }

  /* ---------- 三个动作 ---------- */

  function submit() {
    var content = $("#feedback-content").val().trim();

    if (!content) {
      $("#feedback-error").html(NM.inlineError({ message: "请先写点内容。" }));
      return;
    }

    $("#feedback-error").empty();

    api.post("/api/feedback", {
      content: content,
      kind: Number($("#feedback-kind").val()),
      isAnonymous: $("#feedback-anonymous").is(":checked")
    }).then(function () {
      showList({});
    }, function (error) {
      $("#feedback-error").html(NM.inlineError(error));
    });
  }

  function toggleVote(id) {
    /* 空对象不能省：api.post 把 data 交给 JSON.stringify，undefined 出来的请求体不是合法 JSON。 */
    api.post("/api/feedback/" + id + "/vote", {}).then(function (state) {
      $("[data-action='vote-feedback'][data-feedback-id='" + id + "']")
        .toggleClass("is-voted", state.hasVoted)
        .attr("aria-pressed", state.hasVoted ? "true" : "false")
        .find(".vote-count").text(state.voteCount);
    }, function (error) {
      $("#feedback-list").prepend(NM.inlineError(error));
    });
  }

  function setStatus(id, status) {
    api.put("/api/feedback/" + id + "/status", { status: Number(status) }).then(function () {
      showList(currentPage());
    }, function (error) {
      $("#feedback-list").prepend(NM.inlineError(error));
    });
  }

  function remove(id) {
    if (!confirm("删除之后这条反馈和它的赞同一起消失，不能恢复。确定删掉吗？")) {
      return;
    }

    api.del("/api/feedback/" + id).then(function () {
      showList(currentPage());
    }, function (error) {
      $("#feedback-list").prepend(NM.inlineError(error));
    });
  }

  return {
    showList: showList,
    submit: submit,
    toggleVote: toggleVote,
    setStatus: setStatus,
    remove: remove
  };
})(jQuery);

/* 渲染小工具。挂到 window.NM，给所有页面共用。
 *
 * 这里的东西原本长在 app.js 里，商品页面也要用同一套，就搬了出来——
 * 复制一份的话，转义规则迟早在两份之间跑偏。 */
window.NM = (function () {
  "use strict";

  // 服务端来的字符串插进 HTML 前一律先过这里。这个站点所有会显示用户输入的地方都必须调它：
  // 标题、描述、昵称、错误详情。漏一处就是一处 XSS。
  function esc(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  // 接口发的是北京时间的墙上时钟字符串（"2026-09-29T13:43:09.037"，无时区后缀）。
  // 直接按文本重排，不构造 Date——构造了反而会被浏览器按本地时区再解释一次。
  function formatDateTime(value) {
    return value ? String(value).replace("T", " ").slice(0, 16) : "—";
  }

  function formatPrice(value) {
    var amount = Number(value);

    return "¥" + (isFinite(amount) ? amount.toFixed(2) : "0.00");
  }

  function loading() {
    return '<div class="loading">加载中…</div>';
  }

  function empty(text) {
    return '<div class="empty">' + esc(text) + '</div>';
  }

  /* 错误卡。服务端的失败响应都带 code 和 traceId，这两个是排障时唯一能拿去搜日志的东西，
   * 所以只要有就显示出来。 */
  function errorCard(error) {
    return '<div class="error-card"><h3>出错了</h3>' +
      '<p>' + esc(error.message) + '</p>' +
      (error.code ? '<div class="error-meta">code: <code>' + esc(error.code) + '</code>' +
        (error.traceId ? ' · traceId: <code>' + esc(error.traceId) + '</code>' : '') + '</div>' : '') +
      '<button class="button" data-reload>重试</button>' +
      '</div>';
  }

  /* 表单里的行内错误，比错误卡轻，用在提交失败时。 */
  function inlineError(error) {
    return '<div class="form-error">' + esc(error.message) +
      (error.code ? ' <span class="muted">(' + esc(error.code) + ')</span>' : '') + '</div>';
  }

  function pagerHtml(result, buildHref) {
    if (result.totalPages <= 1) {
      return '<div class="pager"><span class="pager-status">共 ' + result.totalCount + ' 条</span></div>';
    }

    var previous = result.page <= 1;
    var next = result.page >= result.totalPages;

    return '<div class="pager">' +
      '<span class="pager-status">共 ' + result.totalCount + ' 条 · 第 ' +
      result.page + '/' + result.totalPages + ' 页</span>' +
      '<div class="pager-actions">' +
      '<a class="button' + (previous ? " is-disabled" : "") + '" href="' +
      (previous ? "#" : esc(buildHref(result.page - 1))) + '">上一页</a>' +
      '<a class="button' + (next ? " is-disabled" : "") + '" href="' +
      (next ? "#" : esc(buildHref(result.page + 1))) + '">下一页</a>' +
      '</div></div>';
  }

  /* 成色。枚举在接口上是数字，这里翻成人话。索引即枚举值，0 号留空。 */
  var CONDITIONS = ["", "全新", "几乎全新", "轻微使用痕迹", "明显使用痕迹"];

  function conditionText(value) {
    return CONDITIONS[value] || "未知成色";
  }

  function conditionOptions(selected) {
    return CONDITIONS.slice(1).map(function (text, index) {
      var value = index + 1;
      return '<option value="' + value + '"' + (value === selected ? " selected" : "") + '>' +
        esc(text) + '</option>';
    }).join("");
  }

  /* 商品状态。同样按枚举值索引。 */
  var STATUSES = ["", "草稿", "在售", "已售出", "已下架"];

  function statusText(value) {
    return STATUSES[value] || "未知状态";
  }

  function statusBadge(value) {
    var kind = value === 2 ? "badge-ok" : (value === 3 ? "badge-sold" : "badge");

    return '<span class="badge ' + kind + '">' + esc(statusText(value)) + '</span>';
  }

  /* 分类是自引用树，接口给的是扁平表。转成带缩进的 <option>，够用且不用递归渲染。 */
  function categoryOptions(categories, selected) {
    var byParent = {};

    categories.forEach(function (item) {
      var key = item.parentId == null ? "root" : String(item.parentId);
      (byParent[key] = byParent[key] || []).push(item);
    });

    var html = "";

    function walk(parentKey, depth) {
      (byParent[parentKey] || []).forEach(function (item) {
        html += '<option value="' + item.id + '"' + (item.id === selected ? " selected" : "") + '>' +
          esc("　".repeat(depth) + item.name) + '</option>';

        walk(String(item.id), depth + 1);
      });
    }

    walk("root", 0);

    return html;
  }

  function areaOptions(areas, selected) {
    return areas.map(function (item) {
      return '<option value="' + item.id + '"' + (item.id === selected ? " selected" : "") + '>' +
        esc(item.name) + '</option>';
    }).join("");
  }

  return {
    esc: esc,
    formatDateTime: formatDateTime,
    formatPrice: formatPrice,
    loading: loading,
    empty: empty,
    errorCard: errorCard,
    inlineError: inlineError,
    pagerHtml: pagerHtml,
    conditionText: conditionText,
    conditionOptions: conditionOptions,
    statusText: statusText,
    statusBadge: statusBadge,
    categoryOptions: categoryOptions,
    areaOptions: areaOptions
  };
})();

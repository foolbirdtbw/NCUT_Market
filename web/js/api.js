/* 全站唯一的请求入口。
 *
 * 失败响应都是 application/problem+json，带 detail / code / traceId，
 * 在这里解析一次，调用点就只管接一个带 message 的 Error。 */
window.api = function (url, data) {
  return $.ajax({ url: url, data: data, dataType: "json" }).then(null, function (xhr) {
    var problem = xhr.responseJSON || {};
    var error = new Error(problem.detail || problem.title || "请求失败（HTTP " + xhr.status + "）");

    error.code = problem.code;
    error.traceId = problem.traceId;

    throw error;
  });
};

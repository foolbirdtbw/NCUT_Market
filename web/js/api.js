/* 全站唯一的请求入口。
 *
 * 失败响应都是 application/problem+json，带 detail / code / traceId，
 * 在这里解析一次，调用点就只管接一个带 message 的 Error。
 *
 * token 的存取在 auth.js 里，这里只负责带上它——两处都写 localStorage
 * 的话，键名和清理时机迟早会不一致。 */
window.api = (function ($) {
  "use strict";

  function token() {
    return window.auth ? window.auth.token() : null;
  }

  /* 校验失败（400 VALIDATION_ERROR）没有 detail，原因在 errors 里，键是字段名。
   * 不取它的话，密码填短了看到的是 "One or more validation errors occurred."——
   * 既没说是哪个字段，也没说错在哪，等于没说。 */
  function validationMessages(problem) {
    var errors = problem.errors;
    var messages = [];

    if (errors) {
      Object.keys(errors).forEach(function (field) {
        (errors[field] || []).forEach(function (text) {
          messages.push(text);
        });
      });
    }

    return messages;
  }

  function toError(xhr) {
    var problem = xhr.responseJSON || {};
    var messages = validationMessages(problem);
    var error = new Error(
      problem.detail ||
      (messages.length ? messages.join(" ") : "") ||
      problem.title ||
      "请求失败（HTTP " + xhr.status + "）");

    error.status = xhr.status;
    error.code = problem.code;
    error.traceId = problem.traceId;

    return error;
  }

  function fail(url, xhr) {
    /* 401 有两种意思，必须分开：
     *   - 登录接口返回 401 = 账号密码不对，是这一页的正常结果，不能把人踢走；
     *   - 其它接口返回 401 = token 没了或过期了，该清掉并跳登录页。
     * 用 code 判断，不用 URL——INVALID_CREDENTIALS 只在登录时出现。 */
    if (xhr.status === 401 && window.auth && (xhr.responseJSON || {}).code !== "INVALID_CREDENTIALS") {
      window.auth.expire();
    }

    return toError(xhr);
  }

  function request(method, url, data) {
    var settings = { url: url, method: method, dataType: "json" };

    if (data != null) {
      if (method === "GET") {
        settings.data = data;
      } else {
        settings.contentType = "application/json";
        settings.data = JSON.stringify(data);
      }
    }

    var current = token();

    if (current) {
      settings.headers = { Authorization: "Bearer " + current };
    }

    return $.ajax(settings).then(null, function (xhr) {
      throw fail(url, xhr);
    });
  }

  return {
    get: function (url, data) { return request("GET", url, data); },
    post: function (url, data) { return request("POST", url, data); },
    put: function (url, data) { return request("PUT", url, data); },

    del: function (url) { return request("DELETE", url, null); },

    /* 传图。必须 processData/contentType 都关掉，否则 jQuery 会把 FormData 序列化成
     * 字符串并自己填一个 Content-Type，multipart 的 boundary 就没了，服务端收到的是空文件。 */
    upload: function (url, formData, onProgress) {
      var settings = {
        url: url,
        method: "POST",
        data: formData,
        processData: false,
        contentType: false,
        dataType: "json"
      };

      var current = token();

      if (current) {
        settings.headers = { Authorization: "Bearer " + current };
      }

      if (onProgress) {
        settings.xhr = function () {
          var xhr = $.ajaxSettings.xhr();

          if (xhr.upload) {
            xhr.upload.onprogress = function (event) {
              if (event.lengthComputable) {
                onProgress(Math.round(event.loaded / event.total * 100));
              }
            };
          }

          return xhr;
        };
      }

      return $.ajax(settings).then(null, function (xhr) {
        throw fail(url, xhr);
      });
    }
  };
})(jQuery);
